using System.Runtime.Versioning;

namespace P2FK.IO.Services
{
    /// <summary>
    /// Background service that proactively warms the search cache for the default
    /// Bitcoin testnet wildcard query, preventing the first real user request from
    /// having to wait for the full index scan.
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
        private static readonly (int qty, bool showSystemFiles)[] DefaultBatches =
        [
            (200, false),   // index.html  — API_FETCH_BATCH_SIZE=200, chkSystem unchecked by default
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
            foreach (var (qty, showSystemFiles) in DefaultBatches)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    await _searchService.SearchRootsAsync("*", qty, 0, "BTC-testnet", showSystemFiles, forceRefresh: true);
                    _logger.LogDebug(
                        "Cache warmed: BTC-testnet * qty={Qty} showSystemFiles={Show}", qty, showSystemFiles);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Cache warming failed for BTC-testnet * qty={Qty} showSystemFiles={Show}", qty, showSystemFiles);
                }
            }
        }
    }
}
