using P2FK.IO.Models;
using System.Text.RegularExpressions;

namespace P2FK.IO.Services
{
    /// <summary>
    /// Tracks successful free-text root searches for a rolling 24-hour window and
    /// exposes a spam-resistant trending list for the API.
    /// </summary>
    public class RootSearchTrendService
    {
        private const int MaxEntries = 100;
        private const double MaxResultWeight = 0.65;
        private const double AverageResultWeight = 0.35;
        private const double ResultSignalWeight = 1.6;
        private const double SearchSignalWeight = 0.85;
        private static readonly TimeSpan EntryTtl = TimeSpan.FromHours(24);
        private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

        private readonly object _gate = new();
        private readonly Dictionary<string, TrendState> _entries = new(StringComparer.OrdinalIgnoreCase);

        private sealed class TrendState
        {
            public string SearchString { get; set; } = "";
            public int SuccessfulSearchCount { get; set; }
            public int LastResultCount { get; set; }
            public long TotalResultCount { get; set; }
            public int MaxResultCount { get; set; }
            public DateTimeOffset FirstSearchedAtUtc { get; set; }
            public DateTimeOffset LastSearchedAtUtc { get; set; }
        }

        public void RecordSuccessfulSearch(string searchString, int resultCount)
        {
            // Only successful searches are tracked; zero-result lookups do not enter or
            // refresh the trending list.
            if (resultCount <= 0) return;

            string normalized = NormalizeSearchString(searchString);
            if (string.IsNullOrWhiteSpace(normalized) || normalized == "*")
                return;

            var now = DateTimeOffset.UtcNow;

            lock (_gate)
            {
                PruneExpiredLocked(now);

                if (!_entries.TryGetValue(normalized, out var entry))
                {
                    entry = new TrendState
                    {
                        SearchString = normalized,
                        FirstSearchedAtUtc = now
                    };
                    _entries[normalized] = entry;
                }

                entry.SuccessfulSearchCount++;
                entry.LastResultCount = resultCount;
                entry.TotalResultCount += resultCount;
                entry.MaxResultCount = Math.Max(entry.MaxResultCount, resultCount);
                entry.LastSearchedAtUtc = now;

                TrimToTopEntriesLocked(now);
            }
        }

        public IReadOnlyList<TrendingRootSearchEntry> GetTrendingSearches(int qty = MaxEntries)
        {
            qty = Math.Clamp(qty, 1, MaxEntries);
            var now = DateTimeOffset.UtcNow;

            lock (_gate)
            {
                PruneExpiredLocked(now);
                TrimToTopEntriesLocked(now);
                return BuildRankedEntriesLocked(now, qty);
            }
        }

        private void PruneExpiredLocked(DateTimeOffset now)
        {
            var expiredKeys = _entries
                .Where(kvp => now - kvp.Value.LastSearchedAtUtc >= EntryTtl)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (string key in expiredKeys)
                _entries.Remove(key);
        }

        private void TrimToTopEntriesLocked(DateTimeOffset now)
        {
            if (_entries.Count <= MaxEntries) return;

            var keepKeys = _entries
                .Select(kvp => new
                {
                    kvp.Key,
                    Score = CalculateScore(kvp.Value, now),
                    kvp.Value.LastSearchedAtUtc,
                    kvp.Value.MaxResultCount,
                    kvp.Value.SuccessfulSearchCount
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.LastSearchedAtUtc)
                .ThenByDescending(x => x.MaxResultCount)
                .ThenByDescending(x => x.SuccessfulSearchCount)
                .Take(MaxEntries)
                .Select(x => x.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var removeKeys = _entries.Keys.Where(key => !keepKeys.Contains(key)).ToList();
            foreach (string key in removeKeys)
                _entries.Remove(key);
        }

        private List<TrendingRootSearchEntry> BuildRankedEntriesLocked(DateTimeOffset now, int qty)
        {
            return _entries
                .Select(kvp =>
                {
                    TrendState entry = kvp.Value;
                    return new
                    {
                        Entry = entry,
                        AverageResultCount = Math.Round(GetAverageResultCount(entry), 2),
                        Score = CalculateScore(entry, now)
                    };
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Entry.LastSearchedAtUtc)
                .ThenByDescending(x => x.Entry.MaxResultCount)
                .ThenByDescending(x => x.Entry.SuccessfulSearchCount)
                .Take(qty)
                .Select((x, index) => new TrendingRootSearchEntry
                {
                    Rank = index + 1,
                    SearchString = x.Entry.SearchString,
                    SuccessfulSearchCount = x.Entry.SuccessfulSearchCount,
                    LastResultCount = x.Entry.LastResultCount,
                    AverageResultCount = x.AverageResultCount,
                    MaxResultCount = x.Entry.MaxResultCount,
                    LastSearchedAtUtc = x.Entry.LastSearchedAtUtc,
                    Score = Math.Round(x.Score, 6)
                })
                .ToList();
        }

        private static double CalculateScore(TrendState entry, DateTimeOffset now)
        {
            double ageHours = Math.Max(0, (now - entry.LastSearchedAtUtc).TotalHours);
            if (ageHours >= EntryTtl.TotalHours)
                return 0;

            double freshness = 1d - (ageHours / EntryTtl.TotalHours);
            double averageResultCount = GetAverageResultCount(entry);

            // Result volume matters more than repeat count, so the score leans toward
            // broad/high-yield searches.  MaxResultCount gets the larger share because a
            // query that can return many rows should outrank one that only becomes popular
            // via repetition, while AverageResultCount keeps the score anchored to typical
            // successful calls.  Both signals are logarithmic so repeated spam has sharply
            // diminishing returns.
            double resultSignal = Math.Log(1d + (entry.MaxResultCount * MaxResultWeight) + (averageResultCount * AverageResultWeight));
            double searchSignal = Math.Log(1d + entry.SuccessfulSearchCount);

            return freshness * ((resultSignal * ResultSignalWeight) + (searchSignal * SearchSignalWeight));
        }

        private static string NormalizeSearchString(string searchString)
        {
            if (string.IsNullOrWhiteSpace(searchString))
                return string.Empty;

            return WhitespaceRegex.Replace(searchString.Trim(), " ");
        }

        private static double GetAverageResultCount(TrendState entry) =>
            entry.SuccessfulSearchCount == 0 ? 0 : (double)entry.TotalResultCount / entry.SuccessfulSearchCount;
    }
}
