using System.Net;
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
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
        private const int MaxTransactionsPerNetworkPerCycle = 8;
        private const int MaxCliTransactionsPerPollCycle = 2;
        private const int PendingRefreshChecksPerPollCycle = 1;
        private const int MaxRetryAttempts = 3;
        private static readonly Regex IpfsUrnRegex = new(
            @"IPFS:\s*(?:\/\/)?(?:ipfs[\\/])?(?<cid>[A-Za-z0-9]+)(?:[\\/][^<>\s&]*)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly Wrapper _wrapper;
        private readonly WindowsSearchService _searchService;
        private readonly IKuboIngressService _kuboIngressService;
        private readonly HttpClient _httpClient;
        private readonly ILogger<LiveMempoolMonitorService> _logger;
        private readonly ConcurrentDictionary<string, MonitorState> _networkStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _pinnedLiveIpfsCids = new(StringComparer.Ordinal);

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
                    int remainingCliBudget = MaxCliTransactionsPerPollCycle;
                    if (remainingCliBudget > 0)
                    {
                        int pendingChecks = await _searchService.ProcessPendingRootCacheRefreshQueueAsync(
                            stoppingToken,
                            Math.Min(PendingRefreshChecksPerPollCycle, remainingCliBudget));
                        remainingCliBudget = Math.Max(0, remainingCliBudget - pendingChecks);
                    }

                    foreach (var network in GetNetworks())
                    {
                        stoppingToken.ThrowIfCancellationRequested();
                        if (remainingCliBudget <= 0)
                            break;

                        remainingCliBudget = await PollNetworkAsync(network, remainingCliBudget, stoppingToken);
                    }

                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task<int> PollNetworkAsync(Wrapper.BlockchainNode network, int remainingCliBudget, CancellationToken cancellationToken)
        {
            IReadOnlyList<string>? currentMempool = await TryGetRawMempoolAsync(network, cancellationToken);
            if (currentMempool == null)
                return remainingCliBudget;

            MonitorState state = _networkStates.GetOrAdd(network.Key, _ => new MonitorState(currentMempool));

            state.EnqueueNewTransactions(currentMempool);

            if (remainingCliBudget <= 0)
                return remainingCliBudget;

            int transactionBudget = Math.Min(MaxTransactionsPerNetworkPerCycle, remainingCliBudget);
            for (int i = 0; i < transactionBudget; i++)
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

                remainingCliBudget--;
                if (remainingCliBudget <= 0)
                    break;
            }

            return remainingCliBudget;
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

            if (!await TryPinRootIpfsCidsAsync(txId, result, cancellationToken))
                return ProcessTransactionResult.Retry;

            _searchService.QueueRootCacheRefresh(txId, result, network.Mainnet, network.Blockchain);
            return ProcessTransactionResult.Success;
        }

        private async Task<bool> TryPinRootIpfsCidsAsync(string txId, string rawJson, CancellationToken cancellationToken)
        {
            try
            {
                foreach (string cid in await ExtractIpfsCidsAsync(txId, rawJson, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (_pinnedLiveIpfsCids.ContainsKey(cid))
                    {
                        if (await _kuboIngressService.IsPinnedAsync(cid, cancellationToken))
                            continue;

                        _pinnedLiveIpfsCids.TryRemove(cid, out _);
                    }

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
                ex is InvalidOperationException or HttpRequestException or JsonException or IOException or UnauthorizedAccessException ||
                (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _logger.LogWarning(ex, "Failed to fetch/pin live-monitor IPFS content");
                return false;
            }
        }

        private async Task<List<string>> ExtractIpfsCidsAsync(string txId, string rawJson, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(rawJson);
            var cids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (document.RootElement.TryGetProperty("Message", out var messageEl))
            {
                foreach (string message in EnumerateMessageStrings(messageEl))
                {
                    AddIpfsCidsFromText(message, cids);
                }
            }

            AddIpfsCidsFromInlineProObjContent(document.RootElement, cids);

            foreach (string fileContent in await ReadRootProObjFileContentsAsync(txId, document.RootElement, cancellationToken))
            {
                AddIpfsCidsFromText(fileContent, cids);
            }

            return cids.ToList();
        }

        private static void AddIpfsCidsFromText(string text, HashSet<string> cids)
        {
            foreach (string scanText in EnumerateIpfsScanTexts(text))
            {
                foreach (Match match in IpfsUrnRegex.Matches(scanText))
                {
                    string cid = match.Groups["cid"].Value.Trim('<', '>', ' ', '\t', '\r', '\n');
                    if (IsValidIpfsCid(cid))
                        cids.Add(cid);
                }
            }
        }

        private async Task<List<string>> ReadRootProObjFileContentsAsync(string txId, JsonElement rootElement, CancellationToken cancellationToken)
        {
            var contents = new List<string>();
            if (string.IsNullOrWhiteSpace(txId))
                return contents;

            string rootFolderPath = Path.Combine(_wrapper.RootPath, txId);
            if (!Directory.Exists(rootFolderPath))
                return contents;

            HashSet<string> inlineTypes = GetInlineProObjTypes(rootElement);
            foreach (string candidateName in EnumerateRootProObjCandidateNames(rootElement))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string safeName = Path.GetFileName(candidateName.Replace('\\', '/'));
                if (string.IsNullOrWhiteSpace(safeName))
                    continue;

                if (TryGetProObjTypeFromFileName(safeName, out string? fileType) &&
                    fileType != null &&
                    inlineTypes.Contains(fileType))
                    continue;

                string filePath = Path.Combine(rootFolderPath, safeName);
                if (!File.Exists(filePath))
                    continue;

                try
                {
                    string fileContent = await File.ReadAllTextAsync(filePath, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(fileContent))
                        contents.Add(fileContent);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Unable to read root file {FilePath} while scanning for live IPFS URNs", filePath);
                }
            }

            return contents;
        }

        private static void AddIpfsCidsFromInlineProObjContent(JsonElement rootElement, HashSet<string> cids)
        {
            foreach (string propertyName in new[] { "PRO", "OBJ" })
            {
                if (!rootElement.TryGetProperty(propertyName, out var valueElement))
                    continue;

                if (valueElement.ValueKind == JsonValueKind.String)
                {
                    string? value = valueElement.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        AddIpfsCidsFromText(value, cids);
                    continue;
                }

                if (valueElement.ValueKind == JsonValueKind.Object || valueElement.ValueKind == JsonValueKind.Array)
                    AddIpfsCidsFromText(valueElement.GetRawText(), cids);
            }
        }

        private static IEnumerable<string> EnumerateRootProObjCandidateNames(JsonElement rootElement)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PRO",
                "OBJ",
                "PRO.json",
                "OBJ.json"
            };

            if (rootElement.TryGetProperty("File", out var fileElement) && fileElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty fileProperty in fileElement.EnumerateObject())
                {
                    if (IsProOrObjFileName(fileProperty.Name))
                        names.Add(fileProperty.Name);
                }
            }

            return names;
        }

        private static bool IsProOrObjFileName(string fileName)
        {
            return TryGetProObjTypeFromFileName(fileName, out _);
        }

        private static bool TryGetProObjTypeFromFileName(string fileName, out string? type)
        {
            type = null;
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            string normalized = Path.GetFileName(fileName.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            string upper = normalized.Trim().ToUpperInvariant();
            if (upper is "PRO" or "PRO.JSON")
            {
                type = "PRO";
                return true;
            }

            if (upper is "OBJ" or "OBJ.JSON")
            {
                type = "OBJ";
                return true;
            }

            return false;
        }

        private static HashSet<string> GetInlineProObjTypes(JsonElement rootElement)
        {
            var inlineTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string propertyName in new[] { "PRO", "OBJ" })
            {
                if (!rootElement.TryGetProperty(propertyName, out var valueElement))
                    continue;

                if (valueElement.ValueKind is JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array)
                    inlineTypes.Add(propertyName);
            }

            return inlineTypes;
        }

        private static IEnumerable<string> EnumerateIpfsScanTexts(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                yield break;

            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (seen.Add(message))
                yield return message;

            string htmlDecoded = WebUtility.HtmlDecode(message);
            if (seen.Add(htmlDecoded))
                yield return htmlDecoded;

            string? urlDecoded = TryUrlDecode(message);
            if (!string.IsNullOrWhiteSpace(urlDecoded) && seen.Add(urlDecoded))
                yield return urlDecoded;

            string? htmlThenUrlDecoded = TryUrlDecode(htmlDecoded);
            if (!string.IsNullOrWhiteSpace(htmlThenUrlDecoded) && seen.Add(htmlThenUrlDecoded))
                yield return htmlThenUrlDecoded;
        }

        private static string? TryUrlDecode(string value)
        {
            if (string.IsNullOrEmpty(value) || !value.Contains('%', StringComparison.Ordinal))
                return null;

            string decoded = WebUtility.UrlDecode(value);
            return string.Equals(decoded, value, StringComparison.Ordinal) ? null : decoded;
        }

        private static bool IsValidIpfsCid(string cid)
        {
            if (string.IsNullOrWhiteSpace(cid))
                return false;

            return Regex.IsMatch(cid, @"^Qm[1-9A-HJ-NP-Za-km-z]{44}$", RegexOptions.CultureInvariant) ||
                   Regex.IsMatch(cid, @"^[bB][A-Za-z2-7]{58,}$", RegexOptions.CultureInvariant);
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
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return false;

                if (document.RootElement.TryGetProperty("TransactionId", out _))
                    return true;

                if (document.RootElement.TryGetProperty("Output", out _))
                    return true;

                return document.RootElement.TryGetProperty("Message", out _) &&
                       document.RootElement.TryGetProperty("Id", out _);
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
            private readonly object _sync = new();
            private readonly Queue<string> _pendingQueue = new();
            private readonly HashSet<string> _pendingSet = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _retryCounts = new(StringComparer.OrdinalIgnoreCase);

            public MonitorState(IEnumerable<string> knownSnapshot)
            {
                KnownSnapshot = new HashSet<string>(knownSnapshot, StringComparer.OrdinalIgnoreCase);
            }

            public HashSet<string> KnownSnapshot { get; private set; }

            public void EnqueueNewTransactions(IEnumerable<string> currentMempool)
            {
                lock (_sync)
                {
                    var current = new HashSet<string>(currentMempool, StringComparer.OrdinalIgnoreCase);
                    foreach (string txId in current.Where(txId => !KnownSnapshot.Contains(txId)))
                    {
                        if (_pendingSet.Add(txId))
                            _pendingQueue.Enqueue(txId);
                    }

                    KnownSnapshot = current;
                }
            }

            public string? TryDequeuePending()
            {
                lock (_sync)
                {
                    while (_pendingQueue.Count > 0)
                    {
                        string txId = _pendingQueue.Dequeue();
                        if (_pendingSet.Remove(txId))
                            return txId;
                    }

                    return null;
                }
            }

            public void Requeue(string txId, int maxRetryAttempts)
            {
                lock (_sync)
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
            }

            public void MarkComplete(string txId)
            {
                lock (_sync)
                {
                    _retryCounts.Remove(txId);
                }
            }
        }
    }
}
