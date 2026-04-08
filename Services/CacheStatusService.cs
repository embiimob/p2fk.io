using System.Collections.Concurrent;

namespace P2FK.IO.Services
{
    /// <summary>
    /// Thread-safe singleton that tracks the current state of the search cache
    /// warm cycle and per-cache-key entry counts.
    ///
    /// Written by <see cref="CacheWarmingService"/> and <see cref="WindowsSearchService"/>;
    /// read by <c>GetCacheStatusController</c> to expose cache health via the API.
    /// </summary>
    public class CacheStatusService
    {
        // ── Warm-cycle timing ──────────────────────────────────────────────────

        private volatile bool _isWarming;
        private long _lastWarmStartedTicks;       // UTC ticks; 0 = never
        private long _lastWarmCompletedTicks;     // UTC ticks; 0 = never
        private long _lastWarmDurationMs;
        private long _nextWarmAtTicks;            // UTC ticks; 0 = unknown
        private long _currentRefreshIntervalMs;   // 0 = not yet computed

        // ── Per-key entry counts ───────────────────────────────────────────────

        private readonly ConcurrentDictionary<string, int> _entryCounts = new(StringComparer.OrdinalIgnoreCase);

        // ── Public read properties ─────────────────────────────────────────────

        /// <summary>True while a warm cycle is in progress.</summary>
        public bool IsWarming => _isWarming;

        /// <summary>When the most recent warm cycle started (UTC); null if never started.</summary>
        public DateTimeOffset? LastWarmStarted =>
            _lastWarmStartedTicks == 0 ? null
            : new DateTimeOffset(Interlocked.Read(ref _lastWarmStartedTicks), TimeSpan.Zero);

        /// <summary>When the most recent warm cycle completed (UTC); null if never completed.</summary>
        public DateTimeOffset? LastWarmCompleted =>
            _lastWarmCompletedTicks == 0 ? null
            : new DateTimeOffset(Interlocked.Read(ref _lastWarmCompletedTicks), TimeSpan.Zero);

        /// <summary>How long the most recent warm cycle took, in milliseconds.</summary>
        public long LastWarmDurationMs => Interlocked.Read(ref _lastWarmDurationMs);

        /// <summary>
        /// When the next warm cycle is scheduled to start (UTC); null if not yet known.
        /// </summary>
        public DateTimeOffset? NextWarmAt =>
            _nextWarmAtTicks == 0 ? null
            : new DateTimeOffset(Interlocked.Read(ref _nextWarmAtTicks), TimeSpan.Zero);

        /// <summary>
        /// The current adaptive refresh interval in milliseconds
        /// (last scan duration + 60 000 ms).  Zero until the first cycle completes.
        /// </summary>
        public long CurrentRefreshIntervalMs => Interlocked.Read(ref _currentRefreshIntervalMs);

        /// <summary>Snapshot of how many entries are stored in each named cache bucket.</summary>
        public IReadOnlyDictionary<string, int> EntryCounts => _entryCounts;

        // ── Write methods (called by CacheWarmingService / WindowsSearchService) ──

        /// <summary>Mark the start of a warm cycle.</summary>
        public void SetWarmStarted()
        {
            _isWarming = true;
            Interlocked.Exchange(ref _lastWarmStartedTicks, DateTimeOffset.UtcNow.Ticks);
        }

        /// <summary>
        /// Mark the end of a warm cycle and record timing for the next scheduled run.
        /// </summary>
        /// <param name="durationMs">How long the warm took in milliseconds.</param>
        /// <param name="refreshIntervalMs">How long to wait before the next warm (durationMs + 60 000).</param>
        public void SetWarmCompleted(long durationMs, long refreshIntervalMs)
        {
            _isWarming = false;
            Interlocked.Exchange(ref _lastWarmCompletedTicks, DateTimeOffset.UtcNow.Ticks);
            Interlocked.Exchange(ref _lastWarmDurationMs, durationMs);
            Interlocked.Exchange(ref _currentRefreshIntervalMs, refreshIntervalMs);
            var nextAt = DateTimeOffset.UtcNow.AddMilliseconds(refreshIntervalMs);
            Interlocked.Exchange(ref _nextWarmAtTicks, nextAt.Ticks);
        }

        /// <summary>Record the number of entries stored under a specific cache key.</summary>
        public void UpdateEntryCount(string cacheKey, int count) =>
            _entryCounts[cacheKey] = count;
    }
}
