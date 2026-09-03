using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace P2FK.IO.Services
{
    public sealed class LiveMempoolMonitorService : BackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

        private readonly Wrapper _wrapper;
        private readonly WindowsSearchService _searchService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<LiveMempoolMonitorService> _logger;
        private readonly Dictionary<string, HashSet<string>> _knownMempools = new(StringComparer.OrdinalIgnoreCase);

        public LiveMempoolMonitorService(
            Wrapper wrapper,
            WindowsSearchService searchService,
            IHttpClientFactory httpClientFactory,
            ILogger<LiveMempoolMonitorService> logger)
        {
            _wrapper = wrapper;
            _searchService = searchService;
            _httpClientFactory = httpClientFactory;
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

        private async Task PollNetworkAsync(MonitorNetwork network, CancellationToken cancellationToken)
        {
            IReadOnlyList<string>? currentMempool = await TryGetRawMempoolAsync(network, cancellationToken);
            if (currentMempool == null)
                return;

            if (!_knownMempools.TryGetValue(network.Key, out var previousSnapshot))
            {
                _knownMempools[network.Key] = new HashSet<string>(currentMempool, StringComparer.OrdinalIgnoreCase);
                return;
            }

            var newTransactions = currentMempool
                .Where(txId => !previousSnapshot.Contains(txId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _knownMempools[network.Key] = new HashSet<string>(currentMempool, StringComparer.OrdinalIgnoreCase);

            foreach (string txId in newTransactions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessTransactionAsync(network, txId, cancellationToken);
            }
        }

        private async Task<IReadOnlyList<string>?> TryGetRawMempoolAsync(MonitorNetwork network, CancellationToken cancellationToken)
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

                using var client = _httpClientFactory.CreateClient();
                using var response = await client.SendAsync(request, cancellationToken);
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
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _logger.LogDebug(ex, "Live mempool RPC poll failed for {Network}", network.Key);
                return null;
            }
        }

        private async Task ProcessTransactionAsync(MonitorNetwork network, string txId, CancellationToken cancellationToken)
        {
            string arguments =
                "--versionbyte " + network.VersionByte +
                " --getrootbytransactionid --password " + network.RpcPassword +
                " --url " + network.RpcUrl +
                " --username " + network.RpcUser +
                " --tid " + txId;

            string result = await _wrapper.RunBackgroundCommandAsync(network.CliPath, arguments, cancellationToken);
            if (!LooksLikeRootJson(result))
                return;

            _searchService.QueueRootCacheRefresh(txId, result, network.Mainnet, network.Blockchain);
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

        private IEnumerable<MonitorNetwork> GetNetworks()
        {
            yield return new MonitorNetwork("btc-mainnet", "BTC", true, _wrapper.ProdRPCURL, _wrapper.ProdRPCUser, _wrapper.ProdRPCPassword, _wrapper.ProdCLIPath, _wrapper.ProdVersionByte);
            yield return new MonitorNetwork("btc-testnet", "BTC", false, _wrapper.TestRPCURL, _wrapper.TestRPCUser, _wrapper.TestRPCPassword, _wrapper.TestCLIPath, _wrapper.TestVersionByte);
            yield return new MonitorNetwork("ltc-mainnet", "LTC", true, _wrapper.LTCRPCURL, _wrapper.LTCRPCUser, _wrapper.LTCRPCPassword, _wrapper.LTCCLIPath, _wrapper.LTCVersionByte);
            yield return new MonitorNetwork("dog-mainnet", "DOG", true, _wrapper.DOGRPCURL, _wrapper.DOGRPCUser, _wrapper.DOGRPCPassword, _wrapper.DOGCLIPath, _wrapper.DOGVersionByte);
            yield return new MonitorNetwork("mzc-mainnet", "MZC", true, _wrapper.MZCRPCURL, _wrapper.MZCRPCUser, _wrapper.MZCRPCPassword, _wrapper.MZCCLIPath, _wrapper.MZCVersionByte);
        }

        private sealed record MonitorNetwork(
            string Key,
            string Blockchain,
            bool Mainnet,
            string RpcUrl,
            string RpcUser,
            string RpcPassword,
            string CliPath,
            string VersionByte);
    }
}
