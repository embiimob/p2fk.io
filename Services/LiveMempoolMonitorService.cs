using Microsoft.Extensions.Options;
using P2FK.IO.Options;
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
        private static readonly TimeSpan LiveCidRetryInterval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan LiveCidRetryWindow = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan LiveCidFetchTimeout = TimeSpan.FromMinutes(2);
        private const string TransferResultsFileName = "transfer-results.txt";
        private const int MaxTransactionsPerNetworkPerCycle = 8;
        private const int MaxCliTransactionsPerPollCycle = 2;
        private const int PendingRefreshChecksPerPollCycle = 1;
        private const int MaxRetryAttempts = 3;
        private static readonly Regex MessageAttachmentRegex = new(@"<<(?<inner>[^>]+)>>", RegexOptions.Compiled);
        private static readonly Regex IpfsUrnRegex = new(
            @"IPFS:\s*(?:\/\/)?(?:ipfs[\\/])?(?<cid>[A-Za-z0-9]+)(?:[\\/][^<>\s&]*)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex IpfsSchemeRegex = new(
            @"ipfs://(?<cid>[A-Za-z0-9]+)(?:[\\/][^<>\s&]*)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex IpfsGatewayPathRegex = new(
            @"(?:^|[?&=/])ipfs[\\/](?<cid>[A-Za-z0-9]+)(?:[\\/][^<>\s&]*)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly Wrapper _wrapper;
        private readonly WindowsSearchService _searchService;
        private readonly IKuboIngressService _kuboIngressService;
        private readonly HttpClient _httpClient;
        private readonly ILogger<LiveMempoolMonitorService> _logger;
        private readonly ConcurrentDictionary<string, MonitorState> _networkStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _pinnedLiveIpfsCids = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, PendingCidRetry> _pendingLiveCidRetries = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _transferResultLogLock = new(1, 1);
        private readonly string _transferResultsPath;

        public LiveMempoolMonitorService(
            Wrapper wrapper,
            WindowsSearchService searchService,
            IKuboIngressService kuboIngressService,
            IHttpClientFactory httpClientFactory,
            IOptions<IpfsIngressOptions> options,
            ILogger<LiveMempoolMonitorService> logger)
        {
            _wrapper = wrapper;
            _searchService = searchService;
            _kuboIngressService = kuboIngressService;
            _httpClient = httpClientFactory.CreateClient();
            _logger = logger;
            _transferResultsPath = Path.Combine(options.Value.RepoPath, "import", TransferResultsFileName);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(StartupDelay, stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    await ProcessPendingLiveCidRetriesAsync(stoppingToken);

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

            MonitorState state = _networkStates.GetOrAdd(network.Key, _ => new MonitorState());

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
            await WriteTransferResultAsync("LIVE-IPFS-ROOT", txId, "PROCESSING", "mempool transaction dequeued for root/IPFS scan", cancellationToken);

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
            {
                bool isTransient = IsTransientCliFailure(result);
                await WriteTransferResultAsync(
                    "LIVE-IPFS-ROOT",
                    txId,
                    isTransient ? "ROOT-RETRY" : "ROOT-IGNORED",
                    isTransient ? "root payload lookup failed transiently; will retry transaction" : "root payload did not match expected root JSON shape",
                    cancellationToken);
                return isTransient ? ProcessTransactionResult.Retry : ProcessTransactionResult.Ignore;
            }

            if (!await TryPinRootIpfsCidsAsync(txId, result, cancellationToken))
            {
                await WriteTransferResultAsync("LIVE-IPFS-ROOT", txId, "PIN-RETRY", "CID pin/fetch stage failed; transaction will be retried", cancellationToken);
                return ProcessTransactionResult.Retry;
            }

            await WriteTransferResultAsync("LIVE-IPFS-ROOT", txId, "PROCESSED", "root scan completed", cancellationToken);
            _searchService.QueueRootCacheRefresh(txId, result, network.Mainnet, network.Blockchain);
            return ProcessTransactionResult.Success;
        }

        private async Task<bool> TryPinRootIpfsCidsAsync(string txId, string rawJson, CancellationToken cancellationToken)
        {
            try
            {
                List<string> cids = await ExtractIpfsCidsAsync(txId, rawJson, cancellationToken);
                if (cids.Count == 0)
                {
                    await WriteTransferResultAsync(
                        "LIVE-IPFS-ROOT",
                        txId,
                        "NO-CID",
                        "mempool root scanned but no IPFS CID was found in Message/PRO/OBJ content",
                        cancellationToken);
                    return true;
                }

                await WriteTransferResultAsync(
                    "LIVE-IPFS-ROOT",
                    txId,
                    "CID-FOUND",
                    $"mempool root scan found {cids.Count:0} CID(s)",
                    cancellationToken);

                foreach (string cid in cids)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (_pinnedLiveIpfsCids.ContainsKey(cid))
                    {
                        if (await _kuboIngressService.IsPinnedAsync(cid, cancellationToken))
                        {
                            await WriteTransferResultAsync("LIVE-IPFS", cid, "ALREADY-PINNED", $"mempool root {txId} CID already pinned", cancellationToken);
                            continue;
                        }

                        _pinnedLiveIpfsCids.TryRemove(cid, out _);
                    }

                    if (await TryEnsureLiveCidPinnedAsync(cid, cancellationToken))
                        continue;

                    await QueueLiveCidRetryAsync(cid, cancellationToken);
                }

                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or HttpRequestException or JsonException or IOException or UnauthorizedAccessException ||
                (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _logger.LogWarning(ex, "Failed to fetch/pin live-monitor IPFS content");
                await WriteTransferResultAsync("LIVE-IPFS-ROOT", txId, "PIN-FAILED", $"CID extraction/pin failed: {ex.Message}", cancellationToken);
                return false;
            }
        }

        private async Task ProcessPendingLiveCidRetriesAsync(CancellationToken cancellationToken)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach ((string cid, PendingCidRetry retry) in _pendingLiveCidRetries.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (retry.NextAttemptUtc > now)
                    continue;

                if (now - retry.FirstSeenUtc > LiveCidRetryWindow)
                {
                    if (_pendingLiveCidRetries.TryRemove(cid, out PendingCidRetry? expiredRetry))
                    {
                        _logger.LogWarning(
                            "Live-monitor IPFS CID {Cid} was not discoverable after {RetryWindowMinutes:0} minutes; stopping retries after {Attempts} attempts",
                            cid,
                            LiveCidRetryWindow.TotalMinutes,
                            expiredRetry?.Attempts ?? retry.Attempts);
                        await WriteTransferResultAsync(
                            "LIVE-IPFS",
                            cid,
                            "RETRY-EXPIRED",
                            $"mempool monitor stopped retrying after {LiveCidRetryWindow.TotalMinutes:0} minutes and {(expiredRetry?.Attempts ?? retry.Attempts):0} attempts",
                            cancellationToken);
                    }

                    continue;
                }

                if (await TryEnsureLiveCidPinnedAsync(cid, cancellationToken))
                {
                    _pendingLiveCidRetries.TryRemove(cid, out _);
                    continue;
                }

                PendingCidRetry updatedRetry = retry.ScheduleNextAttempt(now + LiveCidRetryInterval);
                _pendingLiveCidRetries.TryUpdate(cid, updatedRetry, retry);
                await WriteTransferResultAsync(
                    "LIVE-IPFS",
                    cid,
                    "RETRY-PENDING",
                    $"mempool monitor attempt {updatedRetry.Attempts:0} failed; next attempt in {LiveCidRetryInterval.TotalMinutes:0} minute(s)",
                    cancellationToken);
            }
        }

        private async Task QueueLiveCidRetryAsync(string cid, CancellationToken cancellationToken)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!_pendingLiveCidRetries.TryAdd(cid, new PendingCidRetry(now, now + LiveCidRetryInterval, attempts: 1)))
                return;

            _logger.LogInformation(
                "Queued live-monitor IPFS CID {Cid} for retry every {RetryIntervalMinutes:0} minute(s) for up to {RetryWindowMinutes:0} minutes",
                cid,
                LiveCidRetryInterval.TotalMinutes,
                LiveCidRetryWindow.TotalMinutes);
            await WriteTransferResultAsync(
                "LIVE-IPFS",
                cid,
                "QUEUED",
                $"mempool monitor queued CID for retry every {LiveCidRetryInterval.TotalMinutes:0} minute(s) up to {LiveCidRetryWindow.TotalMinutes:0} minutes",
                cancellationToken);
        }

        private async Task<bool> TryEnsureLiveCidPinnedAsync(string cid, CancellationToken cancellationToken)
        {
            if (!_pinnedLiveIpfsCids.TryAdd(cid, 0))
            {
                try
                {
                    return await _kuboIngressService.IsPinnedAsync(cid, cancellationToken);
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Live-monitor pin-status check failed for in-flight CID {Cid}", cid);
                    return false;
                }
            }

            using var fetchTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            fetchTimeoutCts.CancelAfter(LiveCidFetchTimeout);

            try
            {
                await _kuboIngressService.FetchAsync(cid, fetchTimeoutCts.Token);
                await _kuboIngressService.PinAsync(cid, fetchTimeoutCts.Token);
                _logger.LogInformation("Pinned live-monitor IPFS CID {Cid}", cid);
                await WriteTransferResultAsync("LIVE-IPFS", cid, "PINNED", "mempool monitor fetched and pinned CID", cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && fetchTimeoutCts.IsCancellationRequested)
            {
                _pinnedLiveIpfsCids.TryRemove(cid, out _);
                _logger.LogDebug(
                    "Live-monitor fetch timed out for IPFS CID {Cid} after {TimeoutMinutes:0} minute(s)",
                    cid,
                    LiveCidFetchTimeout.TotalMinutes);
                return false;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _pinnedLiveIpfsCids.TryRemove(cid, out _);
                _logger.LogDebug("Live-monitor fetch/pin was canceled for IPFS CID {Cid}", cid);
                return false;
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or UnauthorizedAccessException)
            {
                _pinnedLiveIpfsCids.TryRemove(cid, out _);
                _logger.LogDebug(ex, "Live-monitor fetch/pin attempt failed for IPFS CID {Cid}", cid);
                return false;
            }
        }

        private async Task WriteTransferResultAsync(string action, string cid, string status, string detail, CancellationToken cancellationToken)
        {
            string line = $"{DateTimeOffset.UtcNow:O}\t{action}\t{cid}\t{status}\t{detail}{Environment.NewLine}";
            try
            {
                string importPath = Path.GetDirectoryName(_transferResultsPath) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(importPath))
                    Directory.CreateDirectory(importPath);

                await _transferResultLogLock.WaitAsync(cancellationToken);
                try
                {
                    await File.AppendAllTextAsync(_transferResultsPath, line, cancellationToken);
                }
                finally
                {
                    _transferResultLogLock.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                bool lockTaken = false;
                try
                {
                    lockTaken = _transferResultLogLock.Wait(TimeSpan.FromMilliseconds(250));
                    if (!lockTaken)
                    {
                        _logger.LogDebug("Skipped canceled live-monitor transfer write for CID {Cid} because transfer-results lock was busy", cid);
                        return;
                    }

                    File.AppendAllText(_transferResultsPath, line);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Unable to write live-monitor transfer result for CID {Cid}", cid);
                }
                finally
                {
                    if (lockTaken)
                        _transferResultLogLock.Release();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Unable to write live-monitor transfer result for CID {Cid}", cid);
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
                    AddIpfsCidsFromMessageAttachmentRefs(message, cids);
                    AddIpfsCidsFromText(message, cids);
                }
            }

            AddIpfsCidsFromInlineProObjContent(document.RootElement, cids);

            await AddIpfsCidsFromRootProObjFilesAsync(txId, document.RootElement, cids, cancellationToken);

            return cids.ToList();
        }

        private static void AddIpfsCidsFromText(string text, HashSet<string> cids)
        {
            foreach (string scanText in EnumerateIpfsScanTexts(text))
            {
                AddIpfsCidsFromMatches(IpfsUrnRegex.Matches(scanText), cids);
                AddIpfsCidsFromMatches(IpfsSchemeRegex.Matches(scanText), cids);
                AddIpfsCidsFromMatches(IpfsGatewayPathRegex.Matches(scanText), cids);
            }
        }

        private static void AddIpfsCidsFromMatches(MatchCollection matches, HashSet<string> cids)
        {
            foreach (Match match in matches)
            {
                string cid = match.Groups["cid"].Value.Trim('<', '>', ' ', '\t', '\r', '\n');
                if (IsValidIpfsCid(cid))
                    cids.Add(cid);
            }
        }

        private static void AddIpfsCidsFromMessageAttachmentRefs(string message, HashSet<string> cids)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            string decoded = WebUtility.HtmlDecode(message);
            foreach (Match match in MessageAttachmentRegex.Matches(decoded))
            {
                string inner = match.Groups["inner"].Value;
                if (string.IsNullOrWhiteSpace(inner))
                    continue;

                string compact = Regex.Replace(inner, @"\s+", string.Empty);
                if (!compact.StartsWith("IPFS:", StringComparison.OrdinalIgnoreCase))
                    continue;

                string raw = compact[5..];
                string cid = raw.Split(['/', '\\'], 2, StringSplitOptions.None)[0];
                if (IsValidIpfsCid(cid))
                    cids.Add(cid);
            }
        }

        private async Task AddIpfsCidsFromRootProObjFilesAsync(
            string txId,
            JsonElement rootElement,
            HashSet<string> cids,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(txId))
                return;

            string rootFolderPath = Path.Combine(_wrapper.RootPath, txId);
            if (!Directory.Exists(rootFolderPath))
                return;

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
                        AddIpfsCidsFromText(fileContent, cids);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Unable to read root file {FilePath} while scanning for live IPFS URNs", filePath);
                }
            }
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

            if (rootElement.TryGetProperty("File", out var fileElement))
            {
                if (fileElement.ValueKind == JsonValueKind.String)
                {
                    string? fileName = fileElement.GetString();
                    if (IsProOrObjFileName(fileName ?? string.Empty))
                        names.Add(fileName!);
                }
                else if (fileElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty fileProperty in fileElement.EnumerateObject())
                    {
                        if (IsProOrObjFileName(fileProperty.Name))
                            names.Add(fileProperty.Name);
                    }
                }
            }

            if (rootElement.TryGetProperty("Files", out var filesElement))
                AddProObjCandidateNames(filesElement, names);

            return names;
        }

        private static void AddProObjCandidateNames(JsonElement filesElement, HashSet<string> names)
        {
            if (filesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in filesElement.EnumerateObject())
                {
                    if (IsProOrObjFileName(property.Name))
                        names.Add(property.Name);

                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        string? stringValue = property.Value.GetString();
                        if (IsProOrObjFileName(stringValue ?? string.Empty))
                            names.Add(stringValue!);
                    }
                }

                return;
            }

            if (filesElement.ValueKind != JsonValueKind.Array)
                return;

            foreach (JsonElement entry in filesElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    string? value = entry.GetString();
                    if (IsProOrObjFileName(value ?? string.Empty))
                        names.Add(value!);
                    continue;
                }

                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (JsonProperty property in entry.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                        continue;

                    string? value = property.Value.GetString();
                    if (IsProOrObjFileName(value ?? string.Empty))
                        names.Add(value!);
                }
            }
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
            string baseName = Path.GetFileNameWithoutExtension(upper);
            if (upper == "PRO" || baseName == "PRO")
            {
                type = "PRO";
                return true;
            }

            if (upper == "OBJ" || baseName == "OBJ")
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

            var entries = new List<string>();
            foreach (JsonElement item in messageEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;

                string? message = item.GetString();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    entries.Add(message);
                    yield return message;
                }
            }

            for (int i = 0; i < entries.Count - 1; i++)
            {
                string current = entries[i];
                string next = entries[i + 1];
                if (!ShouldCombineMessageBoundaryForIpfs(current, next))
                    continue;

                yield return current + next;
                yield return current + "\n" + next;
            }
        }

        private static bool ShouldCombineMessageBoundaryForIpfs(string current, string next)
        {
            if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(next))
                return false;

            string currentTrim = current.TrimEnd();
            string nextTrim = next.TrimStart();
            return currentTrim.Contains("IPFS", StringComparison.OrdinalIgnoreCase) ||
                   nextTrim.Contains("IPFS", StringComparison.OrdinalIgnoreCase) ||
                   currentTrim.Contains("ipfs/", StringComparison.OrdinalIgnoreCase) ||
                   currentTrim.Contains("ipfs\\", StringComparison.OrdinalIgnoreCase);
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
            private bool _hasSeenSnapshot;

            public MonitorState() { }

            public HashSet<string> KnownSnapshot { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

            public void EnqueueNewTransactions(IEnumerable<string> currentMempool)
            {
                lock (_sync)
                {
                    var current = new HashSet<string>(currentMempool, StringComparer.OrdinalIgnoreCase);
                    if (!_hasSeenSnapshot)
                    {
                        foreach (string txId in current)
                        {
                            if (_pendingSet.Add(txId))
                                _pendingQueue.Enqueue(txId);
                        }

                        KnownSnapshot = current;
                        _hasSeenSnapshot = true;
                        return;
                    }

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

        private sealed class PendingCidRetry
        {
            public PendingCidRetry(DateTimeOffset firstSeenUtc, DateTimeOffset nextAttemptUtc, int attempts)
            {
                FirstSeenUtc = firstSeenUtc;
                NextAttemptUtc = nextAttemptUtc;
                Attempts = attempts;
            }

            public DateTimeOffset FirstSeenUtc { get; }
            public DateTimeOffset NextAttemptUtc { get; }
            public int Attempts { get; }

            public PendingCidRetry ScheduleNextAttempt(DateTimeOffset nextAttemptUtc) =>
                new(FirstSeenUtc, nextAttemptUtc, Attempts + 1);
        }
    }
}
