using System.Diagnostics;
using System.Runtime.Versioning;

namespace P2FK.IO.Services
{
    /// <summary>
    /// Background service that proactively warms the search cache for the default
    /// wildcard queries used by index.html.
    ///
    /// <para>
    /// One all-chains scan per cycle covers roots, objects, <em>and</em> profiles.
    /// <see cref="WindowsSearchService"/> automatically partitions each result set by
    /// blockchain and stores per-chain slices in their own cache keys (e.g.
    /// <c>roots:*:btc-testnet:false</c>, <c>objects:*:ltc</c>, …) so chain-specific
    /// wildcard lookups are served instantly.
    /// </para>
    ///
    /// <para>
    /// The warm interval is <em>adaptive</em>: after each cycle completes the service
    /// waits for <c>scanDuration + 60 s</c> before running again.  This means a fast
    /// index (seconds) refreshes frequently while a slow scan (several minutes) waits
    /// longer, avoiding redundant back-to-back scans.  A floor of 65 s prevents
    /// accidental spin-loops.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class CacheWarmingService : BackgroundService
    {
        // Minimum wait between warm cycles regardless of scan speed (ms).
        // Set to 65 s (5 s above the 60 s padding constant) so a very fast scan
        // never produces a sub-60 s interval through rounding.
        private const long MinRefreshIntervalMs = 65_000;

        // Extra padding added on top of the measured scan duration (ms).
        private const long ExtraPaddingMs = 60_000;

        // Brief pause after host startup so routes and services are fully initialised.
        private const int StartupDelaySeconds = 5;

        // One all-chains wildcard scan, system files excluded.
        private const int WarmQty = 5000;
        private const bool WarmShowSystemFiles = false;

        private readonly WindowsSearchService _searchService;
        private readonly CacheStatusService _cacheStatus;
        private readonly ILogger<CacheWarmingService> _logger;

        public CacheWarmingService(
            WindowsSearchService searchService,
            CacheStatusService cacheStatus,
            ILogger<CacheWarmingService> logger)
        {
            _searchService = searchService;
            _cacheStatus = cacheStatus;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(StartupDelaySeconds), stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var sw = Stopwatch.StartNew();
                    _cacheStatus.SetWarmStarted();

                    await WarmAsync(stoppingToken);

                    sw.Stop();
                    long durationMs = sw.ElapsedMilliseconds;
                    long intervalMs = Math.Max(MinRefreshIntervalMs, durationMs + ExtraPaddingMs);

                    _cacheStatus.SetWarmCompleted(durationMs, intervalMs);

                    _logger.LogDebug(
                        "Cache warm complete: durationMs={DurationMs} nextIntervalMs={IntervalMs}",
                        durationMs, intervalMs);

                    await Task.Delay(TimeSpan.FromMilliseconds(intervalMs), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown — suppress so the host exits cleanly.
            }
        }

        private async Task WarmAsync(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            // Warm roots (blockchain = null → single scan, populates all per-chain keys).
            try
            {
                await _searchService.SearchRootsAsync(
                    "*", WarmQty, 0, blockchain: null, WarmShowSystemFiles, forceRefresh: true);
                _logger.LogDebug("Warm: roots * all-chains complete");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Warm: roots * all-chains failed");
            }

            if (ct.IsCancellationRequested) return;

            // Warm objects (blockchain = null → all-chains + per-chain partitions).
            try
            {
                await _searchService.SearchObjectsAsync("*", WarmQty, 0, blockchain: null, forceRefresh: true);
                _logger.LogDebug("Warm: objects * all-chains complete");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Warm: objects * all-chains failed");
            }

            if (ct.IsCancellationRequested) return;

            // Warm profiles (blockchain = null → all-chains + per-chain partitions).
            try
            {
                await _searchService.SearchProfilesAsync("*", WarmQty, 0, blockchain: null, forceRefresh: true);
                _logger.LogDebug("Warm: profiles * all-chains complete");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Warm: profiles * all-chains failed");
            }
        }
    }
}
