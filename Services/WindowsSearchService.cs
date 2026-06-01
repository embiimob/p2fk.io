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
        private record CachedRootEntry(string Blockchain, string TxId, string RawJson, DateTime BlockDate);
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

        /// <summary>
        /// Queues a non-blocking in-memory refresh for any cached root entries that match
        /// the specified transaction id. This is used by direct root lookups so pending
        /// (epoch-date) entries can be replaced with confirmed block data quickly.
        /// </summary>
        public void QueueRootCacheRefresh(string txId, string rawJson)
        {
            if (string.IsNullOrWhiteSpace(txId) || string.IsNullOrWhiteSpace(rawJson))
                return;

            _ = Task.Run(() => RefreshRootCacheEntry(txId, rawJson));
        }

        private void RefreshRootCacheEntry(string txId, string rawJson)
        {
            try
            {
                JsonElement rootEl;
                try { rootEl = JsonSerializer.Deserialize<JsonElement>(rawJson); }
                catch (JsonException) { return; }

                if (!rootEl.TryGetProperty("Output", out var rootOutput) || rootOutput.ValueKind == JsonValueKind.Null)
                    return;

                string detectedBlockchain = DetectFirstOutputAddress(rootEl);
                bool isSystemRoot = IsSystemRoot(rootEl);

                DateTime blockDate = default;
                if (rootEl.TryGetProperty("BlockDate", out var bdProp) && bdProp.ValueKind == JsonValueKind.String)
                    DateTime.TryParse(bdProp.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out blockDate);

                var refreshedEntry = new CachedRootEntry(detectedBlockchain, txId, rawJson, blockDate);

                foreach (string cacheKey in _cacheStatus.EntryCounts.Keys
                             .Where(k => k.StartsWith("roots:", StringComparison.OrdinalIgnoreCase))
                             .ToArray())
                {
                    if (!_cache.TryGetValue(cacheKey, out List<CachedRootEntry>? existing) || existing == null || existing.Count == 0)
                        continue;

                    bool showSystemFiles = ParseShowSystemFilesFromRootCacheKey(cacheKey);
                    string? chainFilter = ParseBlockchainFromRootCacheKey(cacheKey);
                    bool includeEntry = (showSystemFiles || !isSystemRoot) &&
                                        (string.IsNullOrWhiteSpace(chainFilter) ||
                                         string.Equals(chainFilter, detectedBlockchain, StringComparison.OrdinalIgnoreCase));

                    bool replacedAny = false;
                    var updated = new List<CachedRootEntry>(existing.Count + (includeEntry ? 1 : 0));
                    foreach (var entry in existing)
                    {
                        if (entry.TxId.Equals(txId, StringComparison.OrdinalIgnoreCase))
                        {
                            replacedAny = true;
                            continue;
                        }

                        updated.Add(entry);
                    }

                    if (!replacedAny)
                        continue;

                    if (includeEntry)
                        updated.Add(refreshedEntry);

                    SortRootEntries(updated);
                    _cache.Set(cacheKey, updated, new MemoryCacheEntryOptions()
                        .SetSize(1)
                        .SetAbsoluteExpiration(CacheTtl));
                    _cacheStatus.UpdateEntryCount(cacheKey, updated.Count);
                }
            }
            catch (Exception)
            {
                // Intentionally swallow exceptions from background cache refresh.
            }
        }

        private static bool ParseShowSystemFilesFromRootCacheKey(string cacheKey)
        {
            int lastColon = cacheKey.LastIndexOf(':');
            if (lastColon < 0 || lastColon >= cacheKey.Length - 1)
                return true;

            return !cacheKey[(lastColon + 1)..].Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ParseBlockchainFromRootCacheKey(string cacheKey)
        {
            int lastColon = cacheKey.LastIndexOf(':');
            if (lastColon <= 0)
                return null;

            int secondLastColon = cacheKey.LastIndexOf(':', lastColon - 1);
            if (secondLastColon < 0 || secondLastColon >= lastColon - 1)
                return null;

            string chain = cacheKey[(secondLastColon + 1)..lastColon];
            return string.IsNullOrWhiteSpace(chain) ? null : chain;
        }

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
            bool hasCachedEntries = _cache.TryGetValue(cacheKey, out List<CachedRootEntry>? cachedEntries);
            if (!forceRefresh && hasCachedEntries && cachedEntries != null)
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

            // Wildcard user request with no warm cache available yet: return empty rather
            // than blocking the request with a full filesystem scan.  The background
            // CacheWarmingService will populate the cache shortly; forceRefresh=true
            // (used only by the warm service) bypasses this guard to perform the scan.
            if (isWildcard && !forceRefresh)
                return new List<SearchResultRoot>();

            // Text search: use Windows Search so file content is matched; fall back to a
            // filesystem scan if the index is not yet ready.
            // (Wildcard path only reached below when forceRefresh=true — i.e. warm service.)
            List<SearchRow> rows;
            if (isWildcard)
            {
                rows = await DirectFolderScanAsync("ROOT.json");
            }
            else
            {
                string sanitized = Sanitize(searchString ?? "");
                bool hasSearch = !string.IsNullOrWhiteSpace(sanitized);

                string scopeUri = "file:///" + _rootPath.Replace('\\', '/').Replace("'", "''");

                string sql = hasSearch
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

                rows = await Task.Run(() => ExecuteSearchQuery(sql));

                if (rows.Count == 0)
                    rows = await FallbackScanAsync(hasSearch ? "*" : "ROOT.json", sanitized);
            }

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
            bool incrementalWildcardRefresh = forceRefresh && isWildcard && hasCachedEntries && cachedEntries is { Count: > 0 };
            var cachedTxIds = incrementalWildcardRefresh
                ? cachedEntries!.Select(e => e.TxId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in txMap.OrderByDescending(x => x.Value.Modified))
            {
                string txId = kvp.Key;

                // Incremental refresh optimization: wildcard warm scans are ordered newest
                // first, so after we hit already-cached content we can stop scanning.
                if (incrementalWildcardRefresh && cachedTxIds.Contains(txId))
                    break;

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

                // Extract BlockDate from the already-parsed root element.
                // Pending transactions have no confirmed block and carry the Unix epoch
                // (1970-01-01T00:00:00) as their BlockDate; treat any missing or
                // unparseable value the same way.
                DateTime blockDate = default;
                if (rootEl.TryGetProperty("BlockDate", out var bdProp) && bdProp.ValueKind == JsonValueKind.String)
                    DateTime.TryParse(bdProp.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out blockDate);

                entries.Add(new CachedRootEntry(detectedBlockchain, txId, rawJson, blockDate));
            }

            // Incremental warm refresh appends the unchanged previous cache tail so existing
            // results remain available while only newly-discovered rows are scanned.
            if (incrementalWildcardRefresh && cachedEntries != null)
            {
                var newTxIds = entries.Select(e => e.TxId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                entries.AddRange(cachedEntries.Where(e => !newTxIds.Contains(e.TxId)));
            }

            SortRootEntries(entries);

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

            // When this scan included system files (showSystemFiles=true), also derive and
            // store the showSystemFiles=false variants so both cache keys are populated from
            // a single filesystem scan.  This prevents the wildcard guard from returning an
            // empty list for requests that use the opposite showSystemFiles value.
            if (showSystemFiles)
            {
                var filteredEntries = new List<CachedRootEntry>(entries.Count);
                foreach (var e in entries)
                {
                    JsonElement el;
                    try { el = JsonSerializer.Deserialize<JsonElement>(e.RawJson); }
                    catch (JsonException) { continue; }
                    if (!IsSystemRoot(el))
                        filteredEntries.Add(e);
                }

                string filteredKey = $"roots:{searchString?.ToLowerInvariant() ?? ""}:{blockchain?.ToLowerInvariant() ?? ""}:False";
                _cache.Set(filteredKey, filteredEntries, new MemoryCacheEntryOptions()
                    .SetSize(1)
                    .SetAbsoluteExpiration(CacheTtl));
                _cacheStatus.UpdateEntryCount(filteredKey, filteredEntries.Count);

                if (blockchain == null)
                {
                    foreach (var chainGroup in filteredEntries.GroupBy(e => e.Blockchain, StringComparer.OrdinalIgnoreCase))
                    {
                        string chainCacheKey = $"roots:{searchString?.ToLowerInvariant() ?? ""}:{chainGroup.Key.ToLowerInvariant()}:False";
                        var chainList = chainGroup.ToList();
                        _cache.Set(chainCacheKey, chainList, new MemoryCacheEntryOptions()
                            .SetSize(1)
                            .SetAbsoluteExpiration(CacheTtl));
                        _cacheStatus.UpdateEntryCount(chainCacheKey, chainList.Count);
                    }
                }
            }

            return SliceRootResults(entries, skip, qty);
        }

        public async Task<List<SearchResultObject>> SearchObjectsAsync(
            string searchString, int qty, int skip, string? blockchain = null, bool forceRefresh = false)
        {
            qty = Math.Clamp(qty, 1, 5000);
            skip = Math.Clamp(skip, 0, 4999);
            qty = Math.Min(qty, 5000 - skip);

            string cacheKey = $"objects:{searchString?.ToLowerInvariant() ?? ""}:{blockchain?.ToLowerInvariant() ?? ""}";
            bool hasCachedEntries = _cache.TryGetValue(cacheKey, out List<CachedObjectEntry>? cachedEntries);
            if (!forceRefresh && hasCachedEntries && cachedEntries != null)
                return SliceObjectResults(cachedEntries, skip, qty);

            // Detect wildcard "*" early — needed for the all-chains fallback below.
            bool isWildcard = (searchString ?? "").Trim() == "*";

            // Per-chain wildcard cache miss: derive from all-chains warm cache if available.
            if (!forceRefresh && blockchain != null && isWildcard)
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

            // Wildcard user request with no warm cache available yet: return empty.
            if (isWildcard && !forceRefresh)
                return new List<SearchResultObject>();

            // Wildcard warm path (forceRefresh=true) or text search path.
            List<SearchRow> rows;
            if (isWildcard)
            {
                rows = await DirectFolderScanAsync("OBJ.json");
            }
            else
            {
                string sanitized = Sanitize(searchString ?? "");
                bool hasSearch = !string.IsNullOrWhiteSpace(sanitized);

                string scopeUri = "file:///" + _rootPath.Replace('\\', '/').Replace("'", "''");

                string sql = hasSearch
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

                rows = await Task.Run(() => ExecuteSearchQuery(sql));

                if (rows.Count == 0)
                    rows = await FallbackScanAsync(hasSearch ? "*" : "OBJ.json", sanitized);
            }

            // Deduplicate by address (parent folder) so multiple file hits from the same
            // folder are collapsed to one entry, keeping the newest-modified file per address.
            var newestByAddress = new Dictionary<string, SearchRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                string key = ExtractAddressFromPath(row.Path) ?? row.Path;
                if (!newestByAddress.TryGetValue(key, out var existing) || row.Modified > existing.Modified)
                    newestByAddress[key] = row;
            }

            var ordered = newestByAddress.Values
                .OrderByDescending(r => r.Modified)
                .ToList();

            var entries = new List<CachedObjectEntry>();
            bool incrementalWildcardRefresh = forceRefresh && isWildcard && hasCachedEntries && cachedEntries is { Count: > 0 };
            var cachedAddresses = incrementalWildcardRefresh
                ? cachedEntries!.Select(e => e.Address).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in ordered)
            {
                string? address = ExtractAddressFromPath(row.Path);
                if (address == null) continue;

                if (incrementalWildcardRefresh && cachedAddresses.Contains(address))
                    break;

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

            if (incrementalWildcardRefresh && cachedEntries != null)
            {
                var newAddresses = entries.Select(e => e.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
                entries.AddRange(cachedEntries.Where(e => !newAddresses.Contains(e.Address)));
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
            string searchString, int qty, int skip, string? blockchain = null, bool forceRefresh = false)
        {
            qty = Math.Clamp(qty, 1, 5000);
            skip = Math.Clamp(skip, 0, 4999);
            qty = Math.Min(qty, 5000 - skip);

            string cacheKey = $"profiles:{searchString?.ToLowerInvariant() ?? ""}:{blockchain?.ToLowerInvariant() ?? ""}";
            bool hasCachedEntries = _cache.TryGetValue(cacheKey, out List<CachedProfileEntry>? cachedEntries);
            if (!forceRefresh && hasCachedEntries && cachedEntries != null)
                return SliceProfileResults(cachedEntries, skip, qty);

            // Detect wildcard "*" early — needed for the all-chains fallback below.
            bool isWildcard = (searchString ?? "").Trim() == "*";

            // Per-chain wildcard cache miss: derive from all-chains warm cache if available.
            if (!forceRefresh && blockchain != null && isWildcard)
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

            // Wildcard user request with no warm cache available yet: return empty.
            if (isWildcard && !forceRefresh)
                return new List<SearchResultProfile>();

            // Wildcard warm path (forceRefresh=true) or text search path.
            List<SearchRow> rows;
            if (isWildcard)
            {
                rows = await DirectFolderScanAsync("GetProfileByAddress.json");
            }
            else
            {
                string sanitized = Sanitize(searchString ?? "");
                bool hasSearch = !string.IsNullOrWhiteSpace(sanitized);

                string scopeUri = "file:///" + _rootPath.Replace('\\', '/').Replace("'", "''");

                string sql = hasSearch
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

                rows = await Task.Run(() => ExecuteSearchQuery(sql));

                if (rows.Count == 0)
                    rows = await FallbackScanAsync(hasSearch ? "*" : "GetProfileByAddress.json", sanitized);
            }

            // Deduplicate by address (parent folder) so multiple file hits from the same
            // folder are collapsed to one entry, keeping the newest-modified file per address.
            var newestByAddress = new Dictionary<string, SearchRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                string key = ExtractAddressFromPath(row.Path) ?? row.Path;
                if (!newestByAddress.TryGetValue(key, out var existing) || row.Modified > existing.Modified)
                    newestByAddress[key] = row;
            }

            var ordered = newestByAddress.Values
                .OrderByDescending(r => r.Modified)
                .ToList();

            var entries = new List<CachedProfileEntry>();
            bool incrementalWildcardRefresh = forceRefresh && isWildcard && hasCachedEntries && cachedEntries is { Count: > 0 };
            var cachedAddresses = incrementalWildcardRefresh
                ? cachedEntries!.Select(e => e.Address).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in ordered)
            {
                string? address = ExtractAddressFromPath(row.Path);
                if (address == null) continue;

                if (incrementalWildcardRefresh && cachedAddresses.Contains(address))
                    break;

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

            if (incrementalWildcardRefresh && cachedEntries != null)
            {
                var newAddresses = entries.Select(e => e.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
                entries.AddRange(cachedEntries.Where(e => !newAddresses.Contains(e.Address)));
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

        // ── Direct folder scan (wildcard) ──────────────────────────────────────

        /// <summary>
        /// Enumerates the top-level sub-directories of <see cref="_rootPath"/> and
        /// returns one <see cref="SearchRow"/> per directory that contains
        /// <paramref name="jsonFileName"/>.
        ///
        /// This is used for wildcard ("*") queries where no text filtering is needed.
        /// Walking directories directly is:
        /// <list type="bullet">
        ///   <item>Complete — not limited by Windows Search index coverage.</item>
        ///   <item>Exact — one row per folder, no multi-file-per-folder collisions.</item>
        ///   <item>Fast — single <c>Directory.EnumerateDirectories</c> call; no OLE DB.</item>
        /// </list>
        /// Results are returned sorted newest-modified first.
        /// </summary>
        private Task<List<SearchRow>> DirectFolderScanAsync(string jsonFileName)
        {
            return Task.Run(() =>
            {
                var rows = new List<SearchRow>();

                if (!Directory.Exists(_rootPath))
                    return rows;

                foreach (string dir in Directory.EnumerateDirectories(_rootPath))
                {
                    string jsonPath = Path.Combine(dir, jsonFileName);
                    if (!File.Exists(jsonPath)) continue;

                    try
                    {
                        DateTime modified = File.GetLastWriteTimeUtc(jsonPath);
                        rows.Add(new SearchRow(jsonPath, modified));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }

                rows.Sort((a, b) => b.Modified.CompareTo(a.Modified));
                return rows;
            });
        }

        // ── Fallback filesystem scan (text queries when Windows Search unavailable) ──

        /// <summary>
        /// Scans the <c>root</c> directory tree for files matching
        /// <paramref name="fileName"/> (use <c>"*"</c> to search all file types)
        /// whose content contains <paramref name="searchString"/> (case-insensitive).
        /// When <paramref name="searchString"/> is empty or whitespace all matching
        /// files are returned without a content check.
        /// Used for text searches when Windows Search has not yet indexed the files.
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
                    if (rows.Count >= 100_000) break;

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

        private static void SortRootEntries(List<CachedRootEntry> entries)
        {
            // Sort by BlockDate descending so the newest confirmed transactions appear first.
            // Pending transactions carry the Unix epoch (≤ 1970-01-01) as their BlockDate;
            // they are always placed at the top of the list regardless of date order.
            entries.Sort((a, b) =>
            {
                bool aPending = a.BlockDate <= DateTime.UnixEpoch;
                bool bPending = b.BlockDate <= DateTime.UnixEpoch;
                if (aPending == bPending)
                    return b.BlockDate.CompareTo(a.BlockDate);
                return aPending ? -1 : 1;
            });
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
