using Microsoft.Extensions.Caching.Memory;
using P2FK.IO.Models;
using System.Data.OleDb;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace P2FK.IO.Services
{
    [SupportedOSPlatform("windows")]
    public class WindowsSearchService
    {
        private readonly string _rootPath;
        private readonly IMemoryCache _cache;
        private readonly CacheStatusService _cacheStatus;
        // Cache entries live for 5 minutes.  CacheWarmingService now uses an adaptive
        // interval (last scan duration + 60 s) so the TTL acts only as an eviction
        // backstop rather than driving the refresh cadence.
        internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(300);
        private static readonly Regex TxIdRegex = new Regex(@"[0-9a-fA-F]{64}", RegexOptions.Compiled);
        private const int MaxSearchLength = 2048;

        public WindowsSearchService(IMemoryCache cache, Wrapper wrapper, CacheStatusService cacheStatus)
        {
            _cache = cache;
            _rootPath = wrapper.RootPath;
            _cacheStatus = cacheStatus;
        }

        // ── Blockchain detection ───────────────────────────────────────────────

        public static string DetectBlockchain(string address)
        {
            if (string.IsNullOrEmpty(address)) return "Unknown";
            return address[0] switch
            {
                '1' => "BTC",
                'm' or 'n' => "BTC-testnet",
                'L' => "LTC",
                'D' => "DOG",
                'M' => "MZC",
                _ => "Unknown"
            };
        }

        // ── Input sanitisation ─────────────────────────────────────────────────

        /// <summary>
        /// Sanitises a search string for safe embedding in a Windows Search OLE DB
        /// FREETEXT clause.  Single quotes are doubled, semicolons and other
        /// SQL-meta characters are removed, and the result is capped at
        /// <see cref="MaxSearchLength"/> characters.
        /// </summary>
        private static string Sanitize(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            // Enforce maximum length first so subsequent operations are bounded
            if (input.Length > MaxSearchLength)
                input = input[..MaxSearchLength];

            // Escape single quotes (SQL injection mitigation)
            input = input.Replace("'", "''");

            // Remove characters that have special meaning in OLE DB / T-SQL
            input = Regex.Replace(input, @"[;\\/*?<>|]", string.Empty);

            return input;
        }

        // ── Windows Search OLE DB helper ───────────────────────────────────────

        private record SearchRow(string Path, DateTime Modified);

        // Internal cache types — raw JSON strings so the backing JsonDocument is not
        // pinned in the cache for the full TTL.  JsonElement is only materialised during
        // the scope of an individual request, then GC'd.
        private record CachedRootEntry(string Blockchain, string TxId, string RawJson);
        private record CachedObjectEntry(string Blockchain, string Address, string RawJson);
        private record CachedProfileEntry(string Blockchain, string Address, string RawJson);

        [SupportedOSPlatform("windows")]
        private List<SearchRow> ExecuteSearchQuery(string sql)
        {
            var rows = new List<SearchRow>();
            const string connectionString =
                "Provider=Search.CollatorDSO;Extended Properties=\"Application=Windows\";";

            using var connection = new OleDbConnection(connectionString);
            connection.Open();
            using var command = new OleDbCommand(sql, connection);
            using var reader = command.ExecuteReader();
            if (reader == null) return rows;

            while (reader.Read())
            {
                string path = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                DateTime modified = reader.IsDBNull(1) ? DateTime.MinValue : reader.GetDateTime(1);
                if (!string.IsNullOrEmpty(path))
                    rows.Add(new SearchRow(path, modified));
            }

            return rows;
        }

        // ── System-file filter helpers ─────────────────────────────────────────

        private static readonly HashSet<string> SystemExtensions =
            new(StringComparer.OrdinalIgnoreCase) { "SEC", "OBJ", "LST", "BRN", "PRO", "BUY", "GIV" };

        /// <summary>
        /// Returns true when the root should be hidden under the system-file filter.
        /// Matches the same logic used on the client: a root is "system" when its
        /// <c>File</c> object contains any key whose name or extension is one of the
        /// known system types (SEC/OBJ/LST/BRN/PRO/BUY/GIV), or when the message
        /// is blank and there are no attached files.
        /// </summary>
        private static bool IsSystemRoot(JsonElement root)
        {
            var files = new List<string>();
            if (root.TryGetProperty("File", out var fileEl) && fileEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in fileEl.EnumerateObject())
                {
                    if (!string.IsNullOrEmpty(prop.Name) && prop.Name != "SIG")
                        files.Add(prop.Name);
                }
            }

            // Check if any file name or extension matches a system type
            foreach (string f in files)
            {
                string upper = f.ToUpperInvariant();
                if (SystemExtensions.Contains(upper)) return true;
                int dot = upper.LastIndexOf('.');
                if (dot >= 0 && SystemExtensions.Contains(upper[(dot + 1)..])) return true;
            }

            // Empty message with no files is also treated as system
            string message = string.Empty;
            if (root.TryGetProperty("Message", out var msgEl))
            {
                message = msgEl.ValueKind == JsonValueKind.Array
                    ? string.Join("\n", msgEl.EnumerateArray().Select(e => e.GetString() ?? ""))
                    : msgEl.GetString() ?? string.Empty;
            }

            return string.IsNullOrWhiteSpace(message) && files.Count == 0;
        }

        // ── Public search methods ──────────────────────────────────────────────

        public async Task<List<SearchResultRoot>> SearchRootsAsync(
            string searchString, int qty, int skip, string? blockchain = null, bool showSystemFiles = true,
            bool forceRefresh = false)
        {
            qty = Math.Clamp(qty, 1, 5000);
            skip = Math.Clamp(skip, 0, 4999);
            qty = Math.Min(qty, 5000 - skip);

            // qty/skip are intentionally excluded from the cache key: we cache the full
            // filtered list and slice it in memory, eliminating the (qty × skip) key
            // explosion that previously caused unbounded cache growth.
            // All key segments are lower-cased for case-insensitive consistency.

            // Detect wildcard "*" early — needed both for the all-chains fallback below
            // and for SQL query selection later.
            bool isWildcard = (searchString ?? "").Trim() == "*";

            string cacheKey = $"roots:{searchString?.ToLowerInvariant() ?? ""}:{blockchain?.ToLowerInvariant() ?? ""}:{showSystemFiles}";
            if (!forceRefresh && _cache.TryGetValue(cacheKey, out List<CachedRootEntry>? cachedEntries) && cachedEntries != null)
                return SliceRootResults(cachedEntries, skip, qty);

            // Per-chain wildcard cache miss: derive from the all-chains warm cache if it
            // is available rather than falling back to a live Windows Search scan.
            //
            // The warm service always populates the all-chains key ("roots:*::…") with a
            // complete filesystem scan.  A per-chain key ("roots:*:mzc:…") can be absent
            // or stale because:
            //   • A previous user-triggered live scan overwrote it with a partial result
            //     (Windows Search may return fewer rows than the full file count when its
            //     index is still building, and the fallback only fires when rows == 0).
            //   • The per-chain key was evicted from the in-process memory cache while
            //     the all-chains key survived (different access patterns → different LRU
            //     priority under memory pressure).
            //
            // By deriving from the all-chains key we guarantee consistent, complete counts
            // without an extra filesystem scan.
            if (!forceRefresh && blockchain != null && isWildcard)
            {
                string allChainsKey = $"roots:*::{showSystemFiles}";
                if (_cache.TryGetValue(allChainsKey, out List<CachedRootEntry>? allCached) && allCached != null)
                {
                    var filtered = allCached
                        .Where(e => e.Blockchain.Equals(blockchain, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    _cache.Set(cacheKey, filtered, new MemoryCacheEntryOptions()
                        .SetSize(1)
                        .SetAbsoluteExpiration(CacheTtl));
                    _cacheStatus.UpdateEntryCount(cacheKey, filtered.Count);
                    return SliceRootResults(filtered, skip, qty);
                }
            }

            string sanitized = isWildcard ? string.Empty : Sanitize(searchString ?? "");
            bool hasSearch = !isWildcard && !string.IsNullOrWhiteSpace(sanitized);

            // Use a forward-slash SCOPE URI as required by Windows Search; escape any single quotes
            string scopeUri = "file:///" + _rootPath.Replace('\\', '/').Replace("'", "''");

            // For wildcard searches we only need one row per transaction folder.
            // Querying ROOT.json directly yields exactly one row per folder, avoiding the
            // multi-file-per-folder collision that caused a 10 000-row cap to cover far
            // fewer than 10 000 unique transactions when each folder had several files.
            //
            // For text searches we still scan all file types so that PDFs, HTML, and other
            // files in root/{txId}/ folders are matched; the txId is then extracted from
            // the matched path to locate ROOT.json.
            //
            // TOP 100000 gives plenty of head-room (the directory tree currently has ~8 000
            // root folders; 100 000 leaves room for 12× growth before the cap matters again).
            string sql = isWildcard
                ? $"""
                    SELECT TOP 100000 System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
                      AND System.FileName = 'ROOT.json'
                    ORDER BY System.DateModified DESC
                    """
                : hasSearch
                ? $"""
                    SELECT TOP 100000 System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
                      AND FREETEXT('{sanitized}')
                    ORDER BY System.DateModified DESC
                    """
                : $"""
                    SELECT TOP 100000 System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
                      AND System.FileName = 'ROOT.json'
                    ORDER BY System.DateModified DESC
                    """;

            var rows = await Task.Run(() => ExecuteSearchQuery(sql));

            // If Windows Search returned nothing (index not ready or files not yet indexed),
            // fall back to a direct filesystem scan so results are available immediately.
            // For wildcard, enumerate ROOT.json files directly so the fallback also returns
            // exactly one entry per folder and doesn't waste the 100 000-file cap on
            // non-root files.
            if (rows.Count == 0)
                rows = await FallbackScanAsync(
                    hasSearch ? "*" : "ROOT.json",
                    isWildcard ? string.Empty : sanitized);

            // When filtering system files, do one fast pass over the rows Windows Search already
            // returned to identify system transaction IDs purely from the on-disk file names —
            // no ROOT.json I/O required.  System indicator files are stored without an extension
            // (the filename IS the type code, e.g. "OBJ", "SEC") or with a system extension
            // (e.g. "something.OBJ").  Either form is detected here from the path string alone.
            var systemTxIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!showSystemFiles)
            {
                foreach (var row in rows)
                {
                    string ext = Path.GetExtension(row.Path).TrimStart('.').ToUpperInvariant();
                    string name = Path.GetFileNameWithoutExtension(row.Path).ToUpperInvariant();

                    // Extensionless system file: filename is the type code (e.g. the file is just "OBJ")
                    // Extension-based system file: extension is the type code (e.g. "something.OBJ")
                    bool isSystemFile = SystemExtensions.Contains(ext) ||
                                        (ext.Length == 0 && SystemExtensions.Contains(name));
                    if (!isSystemFile) continue;

                    string? sysId = ExtractTransactionId(row.Path);
                    if (sysId != null) systemTxIds.Add(sysId);
                }
            }

            // Deduplicate by transaction ID, keeping the newest-modified row per txid
            var txMap = new Dictionary<string, SearchRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                string? txId = ExtractTransactionId(row.Path);
                if (txId == null) continue;

                if (!txMap.TryGetValue(txId, out var existing) || row.Modified > existing.Modified)
                    txMap[txId] = row;
            }

            var entries = new List<CachedRootEntry>();

            // Build the full filtered list — no early qty/skip break here.
            // Pagination is applied after the cache is populated so a single cache entry
            // serves all skip/qty combinations for the same search+chain+filter tuple.
            foreach (var kvp in txMap.OrderByDescending(x => x.Value.Modified))
            {
                string txId = kvp.Key;

                // Short-circuit: txId already identified as a system transaction from the Windows
                // Search file listing — skip the ROOT.json read entirely, no file I/O needed.
                if (!showSystemFiles && systemTxIds.Contains(txId))
                    continue;

                string rootJsonPath = Path.Combine(_rootPath, txId, "ROOT.json");

                if (!File.Exists(rootJsonPath)) continue;

                // Read raw JSON text — store the string in the cache rather than a deserialized
                // JsonElement so the backing JsonDocument is not pinned for the full cache TTL.
                string rawJson;
                try { rawJson = await File.ReadAllTextAsync(rootJsonPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                // Parse transiently to validate required fields; the JsonElement (and its
                // JsonDocument) go out of scope at the end of this loop body.
                JsonElement rootEl;
                try { rootEl = JsonSerializer.Deserialize<JsonElement>(rawJson); }
                catch (JsonException) { continue; }

                // Skip roots where Output is null or missing
                if (!rootEl.TryGetProperty("Output", out var rootOutput) || rootOutput.ValueKind == JsonValueKind.Null)
                    continue;

                string detectedBlockchain = DetectFirstOutputAddress(rootEl);

                // Filter by blockchain if requested
                if (blockchain != null && !string.Equals(detectedBlockchain, blockchain, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Fallback for transactions not caught by the file-listing scan: handles the
                // edge case where the message is empty and there are no attached files on disk
                // (ROOT.json is the only file in the folder, so no extension clue is available).
                if (!showSystemFiles && IsSystemRoot(rootEl))
                    continue;

                entries.Add(new CachedRootEntry(detectedBlockchain, txId, rawJson));
            }

            _cache.Set(cacheKey, entries, new MemoryCacheEntryOptions()
                .SetSize(1)
                .SetAbsoluteExpiration(CacheTtl));
            _cacheStatus.UpdateEntryCount(cacheKey, entries.Count);

            // When this was an all-chains scan (blockchain == null), also populate the
            // per-chain partition caches so that chain-specific lookups benefit from this
            // single filesystem scan without needing their own separate full scan.
            if (blockchain == null)
            {
                foreach (var chainGroup in entries.GroupBy(e => e.Blockchain, StringComparer.OrdinalIgnoreCase))
                {
                    string chainCacheKey = $"roots:{searchString?.ToLowerInvariant() ?? ""}:{chainGroup.Key.ToLowerInvariant()}:{showSystemFiles}";
                    var chainList = chainGroup.ToList();
                    _cache.Set(chainCacheKey, chainList, new MemoryCacheEntryOptions()
                        .SetSize(1)
                        .SetAbsoluteExpiration(CacheTtl));
                    _cacheStatus.UpdateEntryCount(chainCacheKey, chainList.Count);
                }
            }

            return SliceRootResults(entries, skip, qty);
        }

        public async Task<List<SearchResultObject>> SearchObjectsAsync(
            string searchString, int qty, int skip, string? blockchain = null)
        {
            qty = Math.Clamp(qty, 1, 5000);
            skip = Math.Clamp(skip, 0, 4999);
            qty = Math.Min(qty, 5000 - skip);

            string cacheKey = $"objects:{searchString?.ToLowerInvariant() ?? ""}:{blockchain?.ToLowerInvariant() ?? ""}";
            if (_cache.TryGetValue(cacheKey, out List<CachedObjectEntry>? cachedEntries) && cachedEntries != null)
                return SliceObjectResults(cachedEntries, skip, qty);

            // Detect wildcard "*" early — needed for the all-chains fallback below.
            bool isWildcard = (searchString ?? "").Trim() == "*";

            // Per-chain wildcard cache miss: derive from all-chains warm cache if available.
            if (blockchain != null && isWildcard)
            {
                string allChainsKey = $"objects:*:";
                if (_cache.TryGetValue(allChainsKey, out List<CachedObjectEntry>? allCached) && allCached != null)
                {
                    var filtered = allCached
                        .Where(e => e.Blockchain.Equals(blockchain, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    _cache.Set(cacheKey, filtered, new MemoryCacheEntryOptions()
                        .SetSize(1)
                        .SetAbsoluteExpiration(CacheTtl));
                    _cacheStatus.UpdateEntryCount(cacheKey, filtered.Count);
                    return SliceObjectResults(filtered, skip, qty);
                }
            }

            string sanitized = isWildcard ? string.Empty : Sanitize(searchString ?? "");
            bool hasSearch = !isWildcard && !string.IsNullOrWhiteSpace(sanitized);

            string scopeUri = "file:///" + _rootPath.Replace('\\', '/').Replace("'", "''");

            // For wildcard, enumerate OBJ.json directly: one row per address folder.
            // For text searches, scan all file types so content in any folder file is matched.
            // TOP 100000 gives ample head-room for growth.
            string sql = isWildcard
                ? $"""
                    SELECT TOP 100000 System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
                      AND System.FileName = 'OBJ.json'
                    ORDER BY System.DateModified DESC
                    """
                : hasSearch
                ? $"""
                    SELECT TOP 100000 System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
                      AND FREETEXT('{sanitized}')
                    ORDER BY System.DateModified DESC
                    """
                : $"""
                    SELECT TOP 100000 System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
                      AND System.FileName = 'OBJ.json'
                    ORDER BY System.DateModified DESC
                    """;

            var rows = await Task.Run(() => ExecuteSearchQuery(sql));

            // If Windows Search returned nothing (index not ready or files not yet indexed),
            // fall back to a direct filesystem scan so results are available immediately.
            if (rows.Count == 0)
                rows = await FallbackScanAsync(
                    hasSearch ? "*" : "OBJ.json",
                    isWildcard ? string.Empty : sanitized);

            // Deduplicate by address (parent folder) so multiple file hits from the same
            // folder are collapsed to one entry, keeping the newest-modified file per address.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = rows
                .Where(r => seen.Add(ExtractAddressFromPath(r.Path) ?? r.Path))
                .OrderByDescending(r => r.Modified)
                .ToList();

            var entries = new List<CachedObjectEntry>();

            foreach (var row in ordered)
            {
                string? address = ExtractAddressFromPath(row.Path);
                if (address == null) continue;

                string detectedBlockchain = DetectBlockchain(address);

                // Filter by blockchain if requested — avoids unnecessary file I/O
                if (blockchain != null && !string.Equals(detectedBlockchain, blockchain, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Always load OBJ.json from the address folder, regardless of which file
                // was matched by the search (e.g. a PDF or HTML file in the same folder).
                string objJsonPath = Path.Combine(_rootPath, address, "OBJ.json");
                if (!File.Exists(objJsonPath)) continue;

                string rawJson;
                try { rawJson = await File.ReadAllTextAsync(objJsonPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                JsonElement objEl;
                try { objEl = JsonSerializer.Deserialize<JsonElement>(rawJson); }
                catch (JsonException) { continue; }

                // Skip objects where URN is null, missing, or empty
                if (!objEl.TryGetProperty("URN", out var objUrn) ||
                    objUrn.ValueKind == JsonValueKind.Null ||
                    (objUrn.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(objUrn.GetString())))
                    continue;

                entries.Add(new CachedObjectEntry(detectedBlockchain, address, rawJson));
            }

            _cache.Set(cacheKey, entries, new MemoryCacheEntryOptions()
                .SetSize(1)
                .SetAbsoluteExpiration(CacheTtl));
            _cacheStatus.UpdateEntryCount(cacheKey, entries.Count);

            // When this was an all-chains scan, populate per-chain partition caches
            // so chain-specific lookups can be served from the same scan.
            if (blockchain == null)
            {
                foreach (var chainGroup in entries.GroupBy(e => e.Blockchain, StringComparer.OrdinalIgnoreCase))
                {
                    string chainCacheKey = $"objects:{searchString?.ToLowerInvariant() ?? ""}:{chainGroup.Key.ToLowerInvariant()}";
                    var chainList = chainGroup.ToList();
                    _cache.Set(chainCacheKey, chainList, new MemoryCacheEntryOptions()
                        .SetSize(1)
                        .SetAbsoluteExpiration(CacheTtl));
                    _cacheStatus.UpdateEntryCount(chainCacheKey, chainList.Count);
                }
            }

            return SliceObjectResults(entries, skip, qty);
        }

        public async Task<List<SearchResultProfile>> SearchProfilesAsync(
            string searchString, int qty, int skip, string? blockchain = null)
        {
            qty = Math.Clamp(qty, 1, 5000);
            skip = Math.Clamp(skip, 0, 4999);
            qty = Math.Min(qty, 5000 - skip);

            string cacheKey = $"profiles:{searchString?.ToLowerInvariant() ?? ""}:{blockchain?.ToLowerInvariant() ?? ""}";
            if (_cache.TryGetValue(cacheKey, out List<CachedProfileEntry>? cachedEntries) && cachedEntries != null)
                return SliceProfileResults(cachedEntries, skip, qty);

            // Detect wildcard "*" early — needed for the all-chains fallback below.
            bool isWildcard = (searchString ?? "").Trim() == "*";

            // Per-chain wildcard cache miss: derive from all-chains warm cache if available.
            if (blockchain != null && isWildcard)
            {
                string allChainsKey = $"profiles:*:";
                if (_cache.TryGetValue(allChainsKey, out List<CachedProfileEntry>? allCached) && allCached != null)
                {
                    var filtered = allCached
                        .Where(e => e.Blockchain.Equals(blockchain, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    _cache.Set(cacheKey, filtered, new MemoryCacheEntryOptions()
                        .SetSize(1)
                        .SetAbsoluteExpiration(CacheTtl));
                    _cacheStatus.UpdateEntryCount(cacheKey, filtered.Count);
                    return SliceProfileResults(filtered, skip, qty);
                }
            }

            string sanitized = isWildcard ? string.Empty : Sanitize(searchString ?? "");
            bool hasSearch = !isWildcard && !string.IsNullOrWhiteSpace(sanitized);

            string scopeUri = "file:///" + _rootPath.Replace('\\', '/').Replace("'", "''");

            // For wildcard, enumerate GetProfileByAddress.json directly: one row per address
            // folder.  For text searches, scan all file types.  TOP 100000 for head-room.
            string sql = isWildcard
                ? $"""
                    SELECT TOP 100000 System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
                      AND System.FileName = 'GetProfileByAddress.json'
                    ORDER BY System.DateModified DESC
                    """
                : hasSearch
                ? $"""
                    SELECT TOP 100000 System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
                      AND FREETEXT('{sanitized}')
                    ORDER BY System.DateModified DESC
                    """
                : $"""
                    SELECT TOP 100000 System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
                      AND System.FileName = 'GetProfileByAddress.json'
                    ORDER BY System.DateModified DESC
                    """;

            var rows = await Task.Run(() => ExecuteSearchQuery(sql));

            // If Windows Search returned nothing (index not ready or files not yet indexed),
            // fall back to a direct filesystem scan so results are available immediately.
            if (rows.Count == 0)
                rows = await FallbackScanAsync(
                    hasSearch ? "*" : "GetProfileByAddress.json",
                    isWildcard ? string.Empty : sanitized);

            // Deduplicate by address (parent folder) so multiple file hits from the same
            // folder are collapsed to one entry, keeping the newest-modified file per address.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = rows
                .Where(r => seen.Add(ExtractAddressFromPath(r.Path) ?? r.Path))
                .OrderByDescending(r => r.Modified)
                .ToList();

            var entries = new List<CachedProfileEntry>();

            foreach (var row in ordered)
            {
                string? address = ExtractAddressFromPath(row.Path);
                if (address == null) continue;

                string detectedBlockchain = DetectBlockchain(address);

                // Filter by blockchain if requested — avoids unnecessary file I/O
                if (blockchain != null && !string.Equals(detectedBlockchain, blockchain, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Always load GetProfileByAddress.json from the address folder, regardless of
                // which file was matched by the search (e.g. a PDF or HTML file in the same folder).
                string profileJsonPath = Path.Combine(_rootPath, address, "GetProfileByAddress.json");
                if (!File.Exists(profileJsonPath)) continue;

                string rawJson;
                try { rawJson = await File.ReadAllTextAsync(profileJsonPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                JsonElement profileEl;
                try { profileEl = JsonSerializer.Deserialize<JsonElement>(rawJson); }
                catch (JsonException) { continue; }

                // Skip profiles where URN is null, missing, or empty
                if (!profileEl.TryGetProperty("URN", out var profileUrn) ||
                    profileUrn.ValueKind == JsonValueKind.Null ||
                    (profileUrn.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(profileUrn.GetString())))
                    continue;

                entries.Add(new CachedProfileEntry(detectedBlockchain, address, rawJson));
            }

            _cache.Set(cacheKey, entries, new MemoryCacheEntryOptions()
                .SetSize(1)
                .SetAbsoluteExpiration(CacheTtl));
            _cacheStatus.UpdateEntryCount(cacheKey, entries.Count);

            // When this was an all-chains scan, populate per-chain partition caches.
            if (blockchain == null)
            {
                foreach (var chainGroup in entries.GroupBy(e => e.Blockchain, StringComparer.OrdinalIgnoreCase))
                {
                    string chainCacheKey = $"profiles:{searchString?.ToLowerInvariant() ?? ""}:{chainGroup.Key.ToLowerInvariant()}";
                    var chainList = chainGroup.ToList();
                    _cache.Set(chainCacheKey, chainList, new MemoryCacheEntryOptions()
                        .SetSize(1)
                        .SetAbsoluteExpiration(CacheTtl));
                    _cacheStatus.UpdateEntryCount(chainCacheKey, chainList.Count);
                }
            }

            return SliceProfileResults(entries, skip, qty);
        }

        // ── Fallback filesystem scan ───────────────────────────────────────────

        /// <summary>
        /// Scans the <c>root</c> directory tree for files matching
        /// <paramref name="fileName"/> (use <c>"*"</c> to search all file types)
        /// whose content contains <paramref name="searchString"/> (case-insensitive).
        /// When <paramref name="searchString"/> is empty or whitespace all matching
        /// files are returned without a content check.
        /// Used when Windows Search has not yet indexed the files.
        /// </summary>
        private Task<List<SearchRow>> FallbackScanAsync(string fileName, string searchString)
        {
            return Task.Run(() =>
            {
                var rows = new List<SearchRow>();

                if (!Directory.Exists(_rootPath))
                    return rows;

                bool filterByContent = !string.IsNullOrWhiteSpace(searchString);

                // Use '*' wildcard with SearchOption to avoid manual recursion
                foreach (string filePath in Directory.EnumerateFiles(
                    _rootPath, fileName, SearchOption.AllDirectories))
                {
                    // Hard cap: prevent the in-memory list from growing without bound
                    // when the index is cold and the tree is large.
                    if (rows.Count >= 10_000) break;

                    try
                    {
                        if (filterByContent)
                        {
                            // Skip files larger than 1 MB to avoid loading images, PDFs,
                            // and other large binaries into a managed string on the LOH.
                            if (new FileInfo(filePath).Length > 1_048_576) continue;

                            string content = File.ReadAllText(filePath);
                            if (!content.Contains(searchString, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }

                        DateTime modified = File.GetLastWriteTimeUtc(filePath);
                        rows.Add(new SearchRow(filePath, modified));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Skip unreadable files
                    }
                }

                return rows;
            });
        }

        // ── Path helpers ───────────────────────────────────────────────────────

        private static string? ExtractTransactionId(string path)
        {
            var match = TxIdRegex.Match(path);
            return match.Success ? match.Value : null;
        }

        /// <summary>
        /// Extracts the blockchain address segment from paths of the form
        /// …\root\{address}\filename  (objects and profiles).
        /// The address is the name of the folder that directly contains the file.
        /// </summary>
        private static string? ExtractAddressFromPath(string path)
        {
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return null;
            string parentFolderName = Path.GetFileName(dir);
            return string.IsNullOrEmpty(parentFolderName) ? null : parentFolderName;
        }

        // ── Slice helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Projects a slice of the full cached root list into <see cref="SearchResultRoot"/>
        /// objects.  <see cref="JsonElement"/> is materialised here — it lives only for the
        /// duration of the current request, not for the cache TTL.  Only entries whose cached
        /// JSON can be re-parsed are included (malformed entries are silently dropped).
        /// </summary>
        private static List<SearchResultRoot> SliceRootResults(List<CachedRootEntry> entries, int skip, int qty)
        {
            var results = new List<SearchResultRoot>(qty);
            foreach (var e in entries.Skip(skip))
            {
                if (results.Count >= qty) break;
                try
                {
                    results.Add(new SearchResultRoot
                    {
                        Blockchain = e.Blockchain,
                        Root = JsonSerializer.Deserialize<JsonElement>(e.RawJson)
                    });
                }
                catch (JsonException) { /* skip malformed entry */ }
            }
            return results;
        }

        private static List<SearchResultObject> SliceObjectResults(List<CachedObjectEntry> entries, int skip, int qty)
        {
            var results = new List<SearchResultObject>(qty);
            foreach (var e in entries.Skip(skip))
            {
                if (results.Count >= qty) break;
                try
                {
                    results.Add(new SearchResultObject
                    {
                        Blockchain = e.Blockchain,
                        Object = JsonSerializer.Deserialize<JsonElement>(e.RawJson)
                    });
                }
                catch (JsonException) { /* skip malformed entry */ }
            }
            return results;
        }

        private static List<SearchResultProfile> SliceProfileResults(List<CachedProfileEntry> entries, int skip, int qty)
        {
            var results = new List<SearchResultProfile>(qty);
            foreach (var e in entries.Skip(skip))
            {
                if (results.Count >= qty) break;
                try
                {
                    results.Add(new SearchResultProfile
                    {
                        Blockchain = e.Blockchain,
                        Address = e.Address,
                        Profile = JsonSerializer.Deserialize<JsonElement>(e.RawJson)
                    });
                }
                catch (JsonException) { /* skip malformed entry */ }
            }
            return results;
        }

        private static string DetectFirstOutputAddress(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) return "Unknown";

            // 1. Try Output keys first (object keyed by address → amount)
            if (root.TryGetProperty("Output", out var output) && output.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in output.EnumerateObject())
                {
                    string detected = DetectBlockchain(prop.Name);
                    if (detected != "Unknown")
                        return detected;
                }
            }

            // 2. Fall back to SignedBy (single address string)
            if (root.TryGetProperty("SignedBy", out var signedBy) && signedBy.ValueKind == JsonValueKind.String)
            {
                string detected = DetectBlockchain(signedBy.GetString() ?? "");
                if (detected != "Unknown")
                    return detected;
            }

            // 3. Fall back to Keyword keys (object keyed by address → value)
            if (root.TryGetProperty("Keyword", out var keyword) && keyword.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in keyword.EnumerateObject())
                {
                    string detected = DetectBlockchain(prop.Name);
                    if (detected != "Unknown")
                        return detected;
                }
            }

            return "Unknown";
        }
    }
}
