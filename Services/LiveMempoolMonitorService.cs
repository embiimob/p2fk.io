using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace P2FK.IO.Services
{
    [SupportedOSPlatform("windows")]
    public sealed class LiveMempoolMonitorService : BackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
        private const int MaxTransactionsPerCycle = 8;
        private const int MaxRetryAttempts = 3;
        private static readonly Regex IpfsUrnRegex = new(
            @"IPFS:\s*(?<cid>[A-Za-z0-9]+)(?:[\\/][^<>\s]*)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly Wrapper _wrapper;
        private readonly WindowsSearchService _searchService;
        private readonly IKuboIngressService _kuboIngressService;
        private readonly HttpClient _httpClient;
        private readonly ILogger<LiveMempoolMonitorService> _logger;
        private readonly ConcurrentDictionary<string, MonitorState> _networkStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _pinnedLiveIpfsCids = new(StringComparer.OrdinalIgnoreCase);

        public LiveMempoolMonitorService(
            Wrapper wrapper,
            WindowsSearchService searchService,
            IKuboIngressService kuboIngressService,
            IHttpClientFactory httpClientFactory,
            ILogger<LiveMempoolMonitorService> logger)
        {
            _wrapper = wrapper;
            _searchService = searchService;
            _kuboIngressService = kuboIngressService;
            _httpClient = httpClientFactory.CreateClient();
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(StartupDelay, stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    foreach (var network in GetNetworks())
                    {
                        stoppingToken.ThrowIfCancellationRequested();
                        await PollNetworkAsync(network, stoppingToken);
                    }

                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task PollNetworkAsync(Wrapper.BlockchainNode network, CancellationToken cancellationToken)
        {
            IReadOnlyList<string>? currentMempool = await TryGetRawMempoolAsync(network, cancellationToken);
            if (currentMempool == null)
                return;

            MonitorState state = _networkStates.GetOrAdd(network.Key, _ => new MonitorState(currentMempool));

            foreach (string txId in currentMempool.Where(txId => !state.KnownSnapshot.Contains(txId)).Distinct(StringComparer.OrdinalIgnoreCase))
                state.Enqueue(txId);

            state.ReplaceKnownSnapshot(currentMempool);

            for (int i = 0; i < MaxTransactionsPerCycle; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? txId = state.TryDequeuePending();
                if (txId == null)
                    break;

                ProcessTransactionResult processed = await ProcessTransactionAsync(network, txId, cancellationToken);
                if (processed == ProcessTransactionResult.Retry)
                    state.Requeue(txId, MaxRetryAttempts);
                else
                    state.MarkComplete(txId);
            }
        }

        private async Task<IReadOnlyList<string>?> TryGetRawMempoolAsync(Wrapper.BlockchainNode network, CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, network.RpcUrl)
                {
                    Content = new StringContent(
                        """{"jsonrpc":"1.0","id":"p2fk-io-live-monitor","method":"getrawmempool","params":[]}""",
                        Encoding.UTF8,
                        "application/json")
                };

                string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{network.RpcUser}:{network.RpcPassword}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Live mempool RPC returned HTTP {StatusCode} for {Network}", (int)response.StatusCode, network.Key);
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                if (document.RootElement.TryGetProperty("error", out var errorEl) && errorEl.ValueKind != JsonValueKind.Null)
                {
                    _logger.LogDebug("Live mempool RPC returned an error for {Network}: {Error}", network.Key, errorEl.GetRawText());
                    return null;
                }

                if (!document.RootElement.TryGetProperty("result", out var resultEl) || resultEl.ValueKind != JsonValueKind.Array)
                    return Array.Empty<string>();

                var txIds = new List<string>();
                foreach (var txEl in resultEl.EnumerateArray())
                {
                    string? txId = txEl.GetString();
                    if (!string.IsNullOrWhiteSpace(txId))
                        txIds.Add(txId);
                }

                return txIds;
            }
            catch (Exception ex) when (
                ex is HttpRequestException or JsonException ||
                (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _logger.LogDebug(ex, "Live mempool RPC poll failed for {Network}", network.Key);
                return null;
            }
        }

        private async Task<ProcessTransactionResult> ProcessTransactionAsync(Wrapper.BlockchainNode network, string txId, CancellationToken cancellationToken)
        {
            string result = await _wrapper.RunBackgroundCommandAsync(
                network.CliPath,
                [
                    "--versionbyte", network.VersionByte,
                    "--getrootbytransactionid",
                    "--password", network.RpcPassword,
                    "--url", network.RpcUrl,
                    "--username", network.RpcUser,
                    "--tid", txId
                ],
                cancellationToken);
            if (!LooksLikeRootJson(result))
                return IsTransientCliFailure(result)
                    ? ProcessTransactionResult.Retry
                    : ProcessTransactionResult.Ignore;

            if (!await TryPinMessageIpfsCidsAsync(result, cancellationToken))
                return ProcessTransactionResult.Retry;

            _searchService.QueueRootCacheRefresh(txId, result, network.Mainnet, network.Blockchain);
            return ProcessTransactionResult.Success;
        }

        private async Task<bool> TryPinMessageIpfsCidsAsync(string rawJson, CancellationToken cancellationToken)
        {
            try
            {
                foreach (string cid in ExtractIpfsCids(rawJson))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!_pinnedLiveIpfsCids.TryAdd(cid, 0))
                        continue;

                    try
                    {
                        await _kuboIngressService.FetchAsync(cid, cancellationToken);
                        await _kuboIngressService.PinAsync(cid, cancellationToken);
                        _logger.LogInformation("Pinned live-monitor IPFS CID {Cid}", cid);
                    }
                    catch
                    {
                        _pinnedLiveIpfsCids.TryRemove(cid, out _);
                        throw;
                    }
                }

                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or HttpRequestException or JsonException ||
                (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _logger.LogWarning(ex, "Failed to fetch/pin live-monitor IPFS content");
                return false;
            }
        }

        private static List<string> ExtractIpfsCids(string rawJson)
        {
            try
            {
                using var document = JsonDocument.Parse(rawJson);
                if (!document.RootElement.TryGetProperty("Message", out var messageEl))
                    return [];

                var cids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string message in EnumerateMessageStrings(messageEl))
                {
                    foreach (Match match in IpfsUrnRegex.Matches(message))
                    {
                        string cid = match.Groups["cid"].Value.Trim('<', '>', ' ', '\t', '\r', '\n');
                        if (IsValidIpfsCid(cid))
                            cids.Add(cid);
                    }
                }

                return cids.ToList();
            }
            catch (JsonException)
            {
                return [];
            }
        }

        private static bool IsValidIpfsCid(string cid)
        {
            if (string.IsNullOrWhiteSpace(cid))
                return false;

            return Regex.IsMatch(cid, @"^Qm[1-9A-HJ-NP-Za-km-z]{44}$", RegexOptions.CultureInvariant) ||
                   Regex.IsMatch(cid, @"^[bB][A-Za-z0-9]{20,}$", RegexOptions.CultureInvariant);
        }

        private static IEnumerable<string> EnumerateMessageStrings(JsonElement messageEl)
        {
            if (messageEl.ValueKind == JsonValueKind.String)
            {
                string? message = messageEl.GetString();
                if (!string.IsNullOrWhiteSpace(message))
                    yield return message;
                yield break;
            }

            if (messageEl.ValueKind != JsonValueKind.Array)
                yield break;

            foreach (JsonElement item in messageEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;

                string? message = item.GetString();
                if (!string.IsNullOrWhiteSpace(message))
                    yield return message;
            }
        }

        private static bool LooksLikeRootJson(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
                return false;

            try
            {
                using var document = JsonDocument.Parse(rawJson);
                return document.RootElement.ValueKind == JsonValueKind.Object &&
                       document.RootElement.TryGetProperty("TransactionId", out _);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool IsTransientCliFailure(string result)
        {
            if (string.IsNullOrWhiteSpace(result))
                return false;

            if (result.Contains("request timed out", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("request cancelled", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("request deferred", StringComparison.OrdinalIgnoreCase))
                return true;

            if (result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                return true;

            try
            {
                using var document = JsonDocument.Parse(result);
                if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
                    return false;

                string? first = document.RootElement[0].GetString();
                return !string.IsNullOrWhiteSpace(first) &&
                       first.StartsWith("error:", StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private IEnumerable<Wrapper.BlockchainNode> GetNetworks() => _wrapper.GetBlockchainNodes();

        private enum ProcessTransactionResult
        {
            Ignore,
            Retry,
            Success
        }

        private sealed class MonitorState
        {
            private readonly Queue<string> _pendingQueue = new();
            private readonly HashSet<string> _pendingSet = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _retryCounts = new(StringComparer.OrdinalIgnoreCase);

            public MonitorState(IEnumerable<string> knownSnapshot)
            {
                KnownSnapshot = new HashSet<string>(knownSnapshot, StringComparer.OrdinalIgnoreCase);
            }

            public HashSet<string> KnownSnapshot { get; private set; }

            public void Enqueue(string txId)
            {
                if (_pendingSet.Add(txId))
                    _pendingQueue.Enqueue(txId);
            }

            public void ReplaceKnownSnapshot(IEnumerable<string> knownSnapshot) =>
                KnownSnapshot = new HashSet<string>(knownSnapshot, StringComparer.OrdinalIgnoreCase);

            public string? TryDequeuePending()
            {
                while (_pendingQueue.Count > 0)
                {
                    string txId = _pendingQueue.Dequeue();
                    if (_pendingSet.Remove(txId))
                        return txId;
                }

                return null;
            }

            public void Requeue(string txId, int maxRetryAttempts)
            {
                int retryCount = _retryCounts.TryGetValue(txId, out int current) ? current + 1 : 1;
                if (retryCount > maxRetryAttempts)
                {
                    _retryCounts.Remove(txId);
                    return;
                }

                _retryCounts[txId] = retryCount;
                if (_pendingSet.Add(txId))
                    _pendingQueue.Enqueue(txId);
            }

            public void MarkComplete(string txId) => _retryCounts.Remove(txId);
        }
    }
}
