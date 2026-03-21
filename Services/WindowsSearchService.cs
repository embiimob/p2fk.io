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
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
        private static readonly Regex TxIdRegex = new Regex(@"[0-9a-fA-F]{64}", RegexOptions.Compiled);
        private const int MaxSearchLength = 2048;

        public WindowsSearchService(IMemoryCache cache, Wrapper wrapper)
        {
            _cache = cache;
            _rootPath = wrapper.RootPath;
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

        // ── Public search methods ──────────────────────────────────────────────

        public async Task<List<SearchResultRoot>> SearchRootsAsync(
            string searchString, int qty, int skip, string blockchain = "")
        {
            qty = Math.Clamp(qty, 1, 100);
            skip = Math.Max(skip, 0);

            string cacheKey = $"roots:{searchString.ToLowerInvariant()}:{qty}:{skip}:{blockchain.ToLowerInvariant()}";
            if (_cache.TryGetValue(cacheKey, out List<SearchResultRoot>? cached) && cached != null)
                return cached;

            string sanitized = Sanitize(searchString);
            bool hasSearch = !string.IsNullOrWhiteSpace(sanitized);

            // Use a forward-slash SCOPE URI as required by Windows Search; escape any single quotes
            string scopeUri = "file:///" + _rootPath.Replace('\\', '/').Replace("'", "''");

            string sql = hasSearch
                ? $"""
                    SELECT System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
                      AND System.FileName = 'ROOT.json'
                      AND FREETEXT('{sanitized}')
                    ORDER BY System.DateModified DESC
                    """
                : $"""
                    SELECT System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
                      AND System.FileName = 'ROOT.json'
                    ORDER BY System.DateModified DESC
                    """;

            var rows = await Task.Run(() => ExecuteSearchQuery(sql));

            // If Windows Search returned nothing (index not ready or JSON not yet indexed),
            // fall back to a direct filesystem scan so results are available immediately.
            if (rows.Count == 0)
                rows = await FallbackScanAsync("ROOT.json", sanitized);

            // Deduplicate by transaction ID, keeping the newest-modified row per txid
            var txMap = new Dictionary<string, SearchRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                string? txId = ExtractTransactionId(row.Path);
                if (txId == null) continue;

                if (!txMap.TryGetValue(txId, out var existing) || row.Modified > existing.Modified)
                    txMap[txId] = row;
            }

            var results = new List<SearchResultRoot>();
            int skipped = 0;

            // Order by newest modified date first
            foreach (var kvp in txMap.OrderByDescending(x => x.Value.Modified))
            {
                if (results.Count >= qty) break;

                string txId = kvp.Key;
                string rootJsonPath = Path.Combine(_rootPath, txId, "ROOT.json");

                if (!File.Exists(rootJsonPath)) continue;

                var rootObj = await ReadJsonAsync<JsonElement?>(rootJsonPath);
                if (rootObj == null) continue;

                string detectedBlockchain = DetectFirstOutputAddress(rootObj.Value);

                if (!string.IsNullOrWhiteSpace(blockchain) &&
                    !detectedBlockchain.Equals(blockchain, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (skipped < skip) { skipped++; continue; }

                results.Add(new SearchResultRoot
                {
                    Blockchain = detectedBlockchain,
                    Root = rootObj
                });
            }

            _cache.Set(cacheKey, results, CacheTtl);
            return results;
        }

        public async Task<List<SearchResultObject>> SearchObjectsAsync(
            string searchString, int qty, int skip, string blockchain = "")
        {
            qty = Math.Clamp(qty, 1, 100);
            skip = Math.Max(skip, 0);

            string cacheKey = $"objects:{searchString.ToLowerInvariant()}:{qty}:{skip}:{blockchain.ToLowerInvariant()}";
            if (_cache.TryGetValue(cacheKey, out List<SearchResultObject>? cached) && cached != null)
                return cached;

            string sanitized = Sanitize(searchString);
            bool hasSearch = !string.IsNullOrWhiteSpace(sanitized);

            string scopeUri = "file:///" + _rootPath.Replace('\\', '/').Replace("'", "''");

            string sql = hasSearch
                ? $"""
                    SELECT System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
                      AND System.FileName = 'OBJ.json'
                      AND FREETEXT('{sanitized}')
                    ORDER BY System.DateModified DESC
                    """
                : $"""
                    SELECT System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
                      AND System.FileName = 'OBJ.json'
                    ORDER BY System.DateModified DESC
                    """;

            var rows = await Task.Run(() => ExecuteSearchQuery(sql));

            // If Windows Search returned nothing (index not ready or JSON not yet indexed),
            // fall back to a direct filesystem scan so results are available immediately.
            if (rows.Count == 0)
                rows = await FallbackScanAsync("OBJ.json", sanitized);

            // Deduplicate by file path
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = rows
                .Where(r => seen.Add(r.Path))
                .OrderByDescending(r => r.Modified)
                .ToList();

            var results = new List<SearchResultObject>();
            int skipped = 0;

            foreach (var row in ordered)
            {
                if (results.Count >= qty) break;

                string? address = ExtractAddressFromPath(row.Path);
                if (address == null) continue;

                string detectedBlockchain = DetectBlockchain(address);

                if (!string.IsNullOrWhiteSpace(blockchain) &&
                    !detectedBlockchain.Equals(blockchain, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (skipped < skip) { skipped++; continue; }

                if (!File.Exists(row.Path)) continue;

                var obj = await ReadJsonAsync<JsonElement?>(row.Path);
                if (obj == null) continue;

                results.Add(new SearchResultObject
                {
                    Blockchain = detectedBlockchain,
                    Object = obj
                });
            }

            _cache.Set(cacheKey, results, CacheTtl);
            return results;
        }

        public async Task<List<SearchResultProfile>> SearchProfilesAsync(
            string searchString, int qty, int skip, string blockchain = "")
        {
            qty = Math.Clamp(qty, 1, 100);
            skip = Math.Max(skip, 0);

            string cacheKey = $"profiles:{searchString.ToLowerInvariant()}:{qty}:{skip}:{blockchain.ToLowerInvariant()}";
            if (_cache.TryGetValue(cacheKey, out List<SearchResultProfile>? cached) && cached != null)
                return cached;

            string sanitized = Sanitize(searchString);
            bool hasSearch = !string.IsNullOrWhiteSpace(sanitized);

            string scopeUri = "file:///" + _rootPath.Replace('\\', '/').Replace("'", "''");

            string sql = hasSearch
                ? $"""
                    SELECT System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
                      AND System.FileName = 'GetProfileByAddress.json'
                      AND FREETEXT('{sanitized}')
                    ORDER BY System.DateModified DESC
                    """
                : $"""
                    SELECT System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
                      AND System.FileName = 'GetProfileByAddress.json'
                    ORDER BY System.DateModified DESC
                    """;

            var rows = await Task.Run(() => ExecuteSearchQuery(sql));

            // If Windows Search returned nothing (index not ready or JSON not yet indexed),
            // fall back to a direct filesystem scan so results are available immediately.
            if (rows.Count == 0)
                rows = await FallbackScanAsync("GetProfileByAddress.json", sanitized);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = rows
                .Where(r => seen.Add(r.Path))
                .OrderByDescending(r => r.Modified)
                .ToList();

            var results = new List<SearchResultProfile>();
            int skipped = 0;

            foreach (var row in ordered)
            {
                if (results.Count >= qty) break;

                string? address = ExtractAddressFromPath(row.Path);
                if (address == null) continue;

                string detectedBlockchain = DetectBlockchain(address);

                if (!string.IsNullOrWhiteSpace(blockchain) &&
                    !detectedBlockchain.Equals(blockchain, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (skipped < skip) { skipped++; continue; }

                if (!File.Exists(row.Path)) continue;

                var profile = await ReadJsonAsync<JsonElement?>(row.Path);
                if (profile == null) continue;

                results.Add(new SearchResultProfile
                {
                    Blockchain = detectedBlockchain,
                    Profile = profile
                });
            }

            _cache.Set(cacheKey, results, CacheTtl);
            return results;
        }

        // ── Fallback filesystem scan ───────────────────────────────────────────

        /// <summary>
        /// Scans the <c>root</c> directory tree for files named
        /// <paramref name="fileName"/> whose content contains
        /// <paramref name="searchString"/> (case-insensitive).
        /// When <paramref name="searchString"/> is empty or whitespace all matching
        /// files are returned without a content check.
        /// Used when Windows Search has not yet indexed the JSON files.
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
                    try
                    {
                        if (filterByContent)
                        {
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

        // ── JSON helpers ───────────────────────────────────────────────────────

        private static async Task<T?> ReadJsonAsync<T>(string filePath)
        {
            try
            {
                await using var stream = File.OpenRead(filePath);
                return await JsonSerializer.DeserializeAsync<T>(stream);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return default;
            }
        }

        private static string DetectFirstOutputAddress(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) return "Unknown";
            if (!root.TryGetProperty("Output", out var output)) return "Unknown";
            if (output.ValueKind != JsonValueKind.Object) return "Unknown";

            foreach (var prop in output.EnumerateObject())
                return DetectBlockchain(prop.Name);

            return "Unknown";
        }
    }
}
