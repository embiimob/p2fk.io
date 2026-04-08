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
        // With CacheTtl = 300 s the warm interval is 300 - 60 = 240 s (4 minutes),
        // down from the previous 30-second churn.
        private const int WarmLeadSeconds = 60;

        // Brief pause after host startup so routes and services are fully initialised
        // before the first warm query hits the search index.
        private const int StartupDelaySeconds = 5;

        // Warm up to 5000 results per chain so less-common chains have a deep cache
        // ready before any user request arrives. The API still returns only what the
        // caller requests (qty is clamped at the controller), so existing consumers
        // are unaffected. "BTC-testnet" maps to mainnet=false; the others use blockchain=<chain>.
        private static readonly (string blockchain, int qty, bool showSystemFiles)[] DefaultBatches =
        [
            ("BTC-testnet", 5000, false),
            ("BTC",         5000, false),
            ("LTC",         5000, false),
            ("DOG",         5000, false),
            ("MZC",         5000, false),
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
