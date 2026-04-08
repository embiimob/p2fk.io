using System.Runtime.Versioning;

namespace P2FK.IO.Services
{
    /// <summary>
    /// Background service that proactively warms the search cache for the default
    /// wildcard queries used by index.html, preventing the first real user request
    /// from having to wait for the full index scan.
    ///
    /// One scan per cycle covers ALL blockchains at once. After the master list is
    /// built and system files are filtered out, <see cref="WindowsSearchService"/>
    /// automatically partitions the results by blockchain and stores each chain's
    /// slice in its own cache key (e.g. roots:*:BTC-testnet:false, roots:*:LTC:false,
    /// etc.).  This means a single filesystem scan is enough to pre-populate every
    /// per-chain cache, making chain-specific wildcard lookups instantaneous.
    ///
    /// The warm interval is set to <c>CacheTtl - WarmLeadSeconds</c> so the cache
    /// is refreshed before it expires, ensuring any user who arrives after the
    /// initial warm-up never experiences the cold-cache delay.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class CacheWarmingService : BackgroundService
    {
        // Warm this many seconds before the cache entry would naturally expire.
        // With CacheTtl = 300 s the warm interval is 300 - 60 = 240 s (4 minutes).
        private const int WarmLeadSeconds = 60;

        // Brief pause after host startup so routes and services are fully initialised
        // before the first warm query hits the search index.
        private const int StartupDelaySeconds = 5;

        // One all-chains wildcard scan, system files excluded.  The service passes
        // blockchain = null so that WindowsSearchService performs a single scan and
        // automatically partitions the results into per-chain cache entries.
        private const int WarmQty = 5000;
        private const bool WarmShowSystemFiles = false;

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
            if (ct.IsCancellationRequested) return;
            try
            {
                // blockchain = null → scan all chains in one pass; WindowsSearchService
                // will partition by blockchain and populate every per-chain cache entry.
                await _searchService.SearchRootsAsync(
                    "*", WarmQty, 0, blockchain: null, WarmShowSystemFiles, forceRefresh: true);

                _logger.LogDebug(
                    "Cache warmed: all chains * qty={Qty} showSystemFiles={Show}", WarmQty, WarmShowSystemFiles);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Cache warming failed for all chains * qty={Qty} showSystemFiles={Show}", WarmQty, WarmShowSystemFiles);
            }
        }
    }
}
