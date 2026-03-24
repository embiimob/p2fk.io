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
        private readonly Wrapper _wrapper;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
        private static readonly Regex TxIdRegex = new Regex(@"[0-9a-fA-F]{64}", RegexOptions.Compiled);
        private const int MaxSearchLength = 2048;

        public WindowsSearchService(IMemoryCache cache, Wrapper wrapper)
        {
            _cache = cache;
            _rootPath = wrapper.RootPath;
            _wrapper = wrapper;
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
            string searchString, int qty, int skip)
        {
            qty = Math.Clamp(qty, 1, 100);
            skip = Math.Max(skip, 0);

            string cacheKey = $"roots:{searchString?.ToLowerInvariant() ?? ""}:{qty}:{skip}";
            if (_cache.TryGetValue(cacheKey, out List<SearchResultRoot>? cached) && cached != null)
                return cached;

            string sanitized = Sanitize(searchString ?? "");
            bool hasSearch = !string.IsNullOrWhiteSpace(sanitized);

            // Use a forward-slash SCOPE URI as required by Windows Search; escape any single quotes
            string scopeUri = "file:///" + _rootPath.Replace('\\', '/').Replace("'", "''");

            // When a search string is present, search all files in the scope (not just ROOT.json)
            // so that PDFs, HTML, and other files in root/{txId}/ folders are also matched.
            // The transaction ID is then extracted from the matched file path to locate ROOT.json.
            string sql = hasSearch
                ? $"""
                    SELECT System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
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

            // If Windows Search returned nothing (index not ready or files not yet indexed),
            // fall back to a direct filesystem scan so results are available immediately.
            if (rows.Count == 0)
                rows = await FallbackScanAsync(hasSearch ? "*" : "ROOT.json", sanitized);

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

                // If Block Date is absent from the cached ROOT.json for a sidechain transaction,
                // fetch fresh data directly from the blockchain node so callers receive correct dates.
                if (IsSidechain(detectedBlockchain) && !HasBlockDate(rootObj.Value))
                {
                    var liveRoot = await FetchRootFromBlockchainAsync(txId, detectedBlockchain);
                    if (liveRoot != null)
                        rootObj = liveRoot;
                }

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
            string searchString, int qty, int skip)
        {
            qty = Math.Clamp(qty, 1, 100);
            skip = Math.Max(skip, 0);

            string cacheKey = $"objects:{searchString?.ToLowerInvariant() ?? ""}:{qty}:{skip}";
            if (_cache.TryGetValue(cacheKey, out List<SearchResultObject>? cached) && cached != null)
                return cached;

            string sanitized = Sanitize(searchString ?? "");
            bool hasSearch = !string.IsNullOrWhiteSpace(sanitized);

            string scopeUri = "file:///" + _rootPath.Replace('\\', '/').Replace("'", "''");

            // When a search string is present, search all files in the scope (not just OBJ.json)
            // so that PDFs, HTML, and other files in root/{address}/ folders are also matched.
            // The address is extracted from the matched file path to locate OBJ.json.
            string sql = hasSearch
                ? $"""
                    SELECT System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
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

            // If Windows Search returned nothing (index not ready or files not yet indexed),
            // fall back to a direct filesystem scan so results are available immediately.
            if (rows.Count == 0)
                rows = await FallbackScanAsync(hasSearch ? "*" : "OBJ.json", sanitized);

            // Deduplicate by address (parent folder) so multiple file hits from the same
            // folder are collapsed to one entry, keeping the newest-modified file per address.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = rows
                .Where(r => seen.Add(ExtractAddressFromPath(r.Path) ?? r.Path))
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

                if (skipped < skip) { skipped++; continue; }

                // Always load OBJ.json from the address folder, regardless of which file
                // was matched by the search (e.g. a PDF or HTML file in the same folder).
                string objJsonPath = Path.Combine(_rootPath, address, "OBJ.json");
                if (!File.Exists(objJsonPath)) continue;

                var obj = await ReadJsonAsync<JsonElement?>(objJsonPath);
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
            string searchString, int qty, int skip)
        {
            qty = Math.Clamp(qty, 1, 100);
            skip = Math.Max(skip, 0);

            string cacheKey = $"profiles:{searchString?.ToLowerInvariant() ?? ""}:{qty}:{skip}";
            if (_cache.TryGetValue(cacheKey, out List<SearchResultProfile>? cached) && cached != null)
                return cached;

            string sanitized = Sanitize(searchString ?? "");
            bool hasSearch = !string.IsNullOrWhiteSpace(sanitized);

            string scopeUri = "file:///" + _rootPath.Replace('\\', '/').Replace("'", "''");

            // When a search string is present, search all files in the scope (not just GetProfileByAddress.json)
            // so that PDFs, HTML, and other files in root/{address}/ folders are also matched.
            // The address is extracted from the matched file path to locate GetProfileByAddress.json.
            string sql = hasSearch
                ? $"""
                    SELECT System.ItemPathDisplay, System.DateModified
                    FROM SystemIndex
                    WHERE SCOPE='{scopeUri}'
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

            // If Windows Search returned nothing (index not ready or files not yet indexed),
            // fall back to a direct filesystem scan so results are available immediately.
            if (rows.Count == 0)
                rows = await FallbackScanAsync(hasSearch ? "*" : "GetProfileByAddress.json", sanitized);

            // Deduplicate by address (parent folder) so multiple file hits from the same
            // folder are collapsed to one entry, keeping the newest-modified file per address.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = rows
                .Where(r => seen.Add(ExtractAddressFromPath(r.Path) ?? r.Path))
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

                if (skipped < skip) { skipped++; continue; }

                // Always load GetProfileByAddress.json from the address folder, regardless of
                // which file was matched by the search (e.g. a PDF or HTML file in the same folder).
                string profileJsonPath = Path.Combine(_rootPath, address, "GetProfileByAddress.json");
                if (!File.Exists(profileJsonPath)) continue;

                var profile = await ReadJsonAsync<JsonElement?>(profileJsonPath);
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

        // ── Sidechain date enrichment ──────────────────────────────────────────

        /// <summary>Returns true when the blockchain is one of the supported sidechains.</summary>
        private static bool IsSidechain(string blockchain) =>
            blockchain is "LTC" or "DOG" or "MZC";

        /// <summary>
        /// Returns true when the ROOT JSON element contains a non-empty "Block Date"
        /// (either spaced or camel-case form).
        /// </summary>
        private static bool HasBlockDate(JsonElement root)
        {
            if (root.TryGetProperty("Block Date", out var bd1) &&
                bd1.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(bd1.GetString()))
                return true;

            if (root.TryGetProperty("BlockDate", out var bd2) &&
                bd2.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(bd2.GetString()))
                return true;

            return false;
        }

        /// <summary>
        /// Queries the specified sidechain blockchain node for a root by its transaction ID.
        /// Returns null if the call fails or returns unusable data.
        /// </summary>
        private async Task<JsonElement?> FetchRootFromBlockchainAsync(string txId, string blockchain)
        {
            string? cliPath = blockchain switch
            {
                "LTC" => _wrapper.LTCCLIPath,
                "DOG" => _wrapper.DOGCLIPath,
                "MZC" => _wrapper.MZCCLIPath,
                _ => null
            };

            string? arguments = blockchain switch
            {
                "LTC" => $"--versionbyte {_wrapper.LTCVersionByte} --getrootbytransactionid --password {_wrapper.LTCRPCPassword} --url {_wrapper.LTCRPCURL} --username {_wrapper.LTCRPCUser} --tid {txId}",
                "DOG" => $"--versionbyte {_wrapper.DOGVersionByte} --getrootbytransactionid --password {_wrapper.DOGRPCPassword} --url {_wrapper.DOGRPCURL} --username {_wrapper.DOGRPCUser} --tid {txId}",
                "MZC" => $"--versionbyte {_wrapper.MZCVersionByte} --getrootbytransactionid --password {_wrapper.MZCRPCPassword} --url {_wrapper.MZCRPCURL} --username {_wrapper.MZCRPCUser} --tid {txId}",
                _ => null
            };

            if (cliPath == null || arguments == null) return null;

            try
            {
                string result = await _wrapper.RunCommandAsync(cliPath, arguments);
                var element = JsonSerializer.Deserialize<JsonElement?>(result);
                // Only accept a well-formed JSON object; error strings, arrays, or nulls are discarded
                return element.HasValue && element.Value.ValueKind == JsonValueKind.Object
                    ? element
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
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

            // 2. Fall back to "Signed By" (spaced) or "SignedBy" (camel) – ROOT.json may use either form
            if (root.TryGetProperty("Signed By", out var signedBySpaced) && signedBySpaced.ValueKind == JsonValueKind.String)
            {
                string detected = DetectBlockchain(signedBySpaced.GetString() ?? "");
                if (detected != "Unknown")
                    return detected;
            }

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
