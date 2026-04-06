using System.Runtime.Versioning;

namespace P2FK.IO.Services
{
    /// <summary>
    /// Background service that proactively warms the search cache for the default
    /// wildcard queries used by index.html, preventing the first real user request
    /// from having to wait for the full index scan.
    ///
    /// Covers Bitcoin testnet, Bitcoin mainnet, Litecoin, Dogecoin, and Mazacoin —
    /// matching the chains available via the blockchain checkboxes in index.html.
    ///
    /// The warm interval is set to <c>CacheTtl - WarmLeadSeconds</c> so the cache
    /// is refreshed before it expires, ensuring any user who arrives after the
    /// initial warm-up never experiences the cold-cache delay.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class CacheWarmingService : BackgroundService
    {
        // Warm this many seconds before the cache entry would naturally expire.
        private const int WarmLeadSeconds = 30;

        // Brief pause after host startup so routes and services are fully initialised
        // before the first warm query hits the search index.
        private const int StartupDelaySeconds = 5;

        // index.html uses API_FETCH_BATCH_SIZE=200 with system files hidden by default.
        // Each entry is (blockchain, qty, showSystemFiles).
        // "BTC-testnet" maps to mainnet=false; the others use blockchain=<chain>.
        private static readonly (string blockchain, int qty, bool showSystemFiles)[] DefaultBatches =
        [
            ("BTC-testnet", 200, false),
            ("BTC",         200, false),
            ("LTC",         200, false),
            ("DOG",         200, false),
            ("MZC",         200, false),
        ];

        private readonly WindowsSearchService _searchService;
        private readonly ILogger<CacheWarmingService> _logger;

        public CacheWarmingService(WindowsSearchService searchService, ILogger<CacheWarmingService> logger)
        {
            _searchService = searchService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // Let the host finish starting up before the first warm.
                await Task.Delay(TimeSpan.FromSeconds(StartupDelaySeconds), stoppingToken);

                var interval = WindowsSearchService.CacheTtl - TimeSpan.FromSeconds(WarmLeadSeconds);

                while (!stoppingToken.IsCancellationRequested)
                {
                    await WarmAsync(stoppingToken);
                    await Task.Delay(interval, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown — suppress the exception so the host exits cleanly.
            }
        }

        private async Task WarmAsync(CancellationToken ct)
        {
            foreach (var (blockchain, qty, showSystemFiles) in DefaultBatches)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    await _searchService.SearchRootsAsync("*", qty, 0, blockchain, showSystemFiles, forceRefresh: true);
                    _logger.LogDebug(
                        "Cache warmed: {Chain} * qty={Qty} showSystemFiles={Show}", blockchain, qty, showSystemFiles);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Cache warming failed for {Chain} * qty={Qty} showSystemFiles={Show}", blockchain, qty, showSystemFiles);
                }
            }
        }
    }
}
