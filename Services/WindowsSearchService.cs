using Microsoft.Extensions.Caching.Memory;
using System.Data.OleDb;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace P2FK.IO.Services
{
    [SupportedOSPlatform("windows")]
    public class WindowsSearchService
    {
        private readonly string _rootPath;
        private readonly IMemoryCache _cache;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);
        private const int MaxSearchLength = 2048;
        private static readonly Regex TxIdRegex = new(@"[0-9a-fA-F]{64}", RegexOptions.Compiled);
        private const string OleDbConnectionString =
            "Provider=Search.CollatorDSO;Extended Properties=\"Application=Windows\"";

        public WindowsSearchService(IMemoryCache cache)
        {
            _cache = cache;
            _rootPath = Path.Combine(AppContext.BaseDirectory, "root");
        }

        public static string DetectBlockchain(string address)
        {
            if (string.IsNullOrEmpty(address)) return "Unknown";
            return address[0] switch
            {
                '1' => "BTC",
                'm' or 'n' => "BTC-testnet",
                'L' => "LTC",
                'D' => "DOGE",
                'M' => "MZC",
                _ => "Unknown"
            };
        }

        public static bool MatchesBlockchainFilter(string detectedBlockchain, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            return filter.ToUpperInvariant() switch
            {
                "BTC" => detectedBlockchain == "BTC" || detectedBlockchain == "BTC-testnet",
                "LTC" => detectedBlockchain == "LTC",
                "DOG" => detectedBlockchain == "DOGE",
                "MZC" => detectedBlockchain == "MZC",
                _ => false
            };
        }

        private static string SanitizeSearchString(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;
            if (input.Length > MaxSearchLength)
                input = input[..MaxSearchLength];
            // Escape single quotes by doubling them
            input = input.Replace("'", "''");
            // Strip semicolons and control characters
            input = Regex.Replace(input, @"[;\x00-\x08\x0B\x0C\x0E-\x1F]", "");
            return input;
        }

        private static string EscapeScopePath(string path)
        {
            return path.Replace("'", "''");
        }

        private static string? ExtractAddressFromPath(string path, int afterTxidEnd)
        {
            if (afterTxidEnd >= path.Length) return null;
            string remaining = path[afterTxidEnd..].TrimStart(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(remaining)) return null;
            int sepIndex = remaining.IndexOfAny(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            return sepIndex < 0 ? remaining : remaining[..sepIndex];
        }

        private List<(string path, DateTime modified)> QuerySearchIndex(
            string sanitizedSearch, string? filenameFilter)
        {
            var results = new List<(string path, DateTime modified)>();

            if (!Directory.Exists(_rootPath))
                return results;

            string scopeUri = "file:" + EscapeScopePath(_rootPath);

            string filenameClause = filenameFilter != null
                ? $" AND System.FileName = '{filenameFilter}'"
                : "";

            string sql =
                $"SELECT System.ItemPathDisplay, System.DateModified " +
                $"FROM SystemIndex " +
                $"WHERE SCOPE='{scopeUri}'" +
                $"{filenameClause}" +
                $" AND FREETEXT('{sanitizedSearch}')" +
                $" ORDER BY System.DateModified DESC";

            using var connection = new OleDbConnection(OleDbConnectionString);
            connection.Open();
            using var command = new OleDbCommand(sql, connection);
            using var reader = command.ExecuteReader();
            while (reader != null && reader.Read())
            {
                string? path = reader.IsDBNull(0) ? null : reader.GetString(0);
                DateTime modified = reader.IsDBNull(1) ? DateTime.MinValue : reader.GetDateTime(1);
                if (!string.IsNullOrEmpty(path))
                    results.Add((path, modified));
            }

            return results;
        }

        public async Task<string> SearchRootsAsync(
            string searchString, int qty, int skip, string blockchain)
        {
            string cacheKey =
                $"roots|{searchString.ToLowerInvariant()}|{qty}|{skip}|{blockchain.ToUpperInvariant()}";
            if (_cache.TryGetValue(cacheKey, out string? cached))
                return cached!;

            string sanitized = SanitizeSearchString(searchString);
            if (string.IsNullOrEmpty(sanitized))
                return "[]";

            var rawResults = await Task.Run(() => QuerySearchIndex(sanitized, null));

            // Extract txids, deduplicate by txid keeping newest modified date
            var txidMap = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, modified) in rawResults)
            {
                var match = TxIdRegex.Match(path);
                if (!match.Success) continue;
                string txid = match.Value;
                if (!txidMap.TryGetValue(txid, out DateTime existing) || modified > existing)
                    txidMap[txid] = modified;
            }

            var sortedTxids = txidMap
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList();

            var resultArray = new List<string>();
            int skipped = 0;

            foreach (string txid in sortedTxids)
            {
                if (resultArray.Count >= qty) break;

                string rootJsonPath = Path.Combine(_rootPath, txid, "root.json");
                if (!File.Exists(rootJsonPath)) continue;

                try
                {
                    string fileContent = await File.ReadAllTextAsync(rootJsonPath);
                    var jsonObj = JsonNode.Parse(fileContent)?.AsObject();
                    if (jsonObj == null) continue;

                    string detectedBlockchain = "Unknown";
                    if (jsonObj["Output"] is JsonObject output && output.Count > 0)
                    {
                        string firstAddress = output.First().Key;
                        detectedBlockchain = DetectBlockchain(firstAddress);
                    }

                    if (!MatchesBlockchainFilter(detectedBlockchain, blockchain)) continue;

                    if (skipped < skip)
                    {
                        skipped++;
                        continue;
                    }

                    jsonObj["Blockchain"] = detectedBlockchain;
                    resultArray.Add(jsonObj.ToJsonString());
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
                { /* Skip unreadable or malformed files */ }
            }

            string result = "[" + string.Join(",", resultArray) + "]";
            _cache.Set(cacheKey, result,
                new MemoryCacheEntryOptions { SlidingExpiration = CacheDuration });
            return result;
        }

        public async Task<string> SearchObjectsAsync(
            string searchString, int qty, int skip, string blockchain)
        {
            string cacheKey =
                $"objects|{searchString.ToLowerInvariant()}|{qty}|{skip}|{blockchain.ToUpperInvariant()}";
            if (_cache.TryGetValue(cacheKey, out string? cached))
                return cached!;

            string sanitized = SanitizeSearchString(searchString);
            if (string.IsNullOrEmpty(sanitized))
                return "[]";

            var rawResults = await Task.Run(() => QuerySearchIndex(sanitized, "OBJ.json"));

            // Deduplicate by file path, keeping newest modified date per path
            var pathMap = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, modified) in rawResults)
            {
                var match = TxIdRegex.Match(path);
                if (!match.Success) continue;
                string? address = ExtractAddressFromPath(path, match.Index + match.Length);
                if (string.IsNullOrEmpty(address)) continue;

                if (!pathMap.TryGetValue(path, out DateTime existing) || modified > existing)
                    pathMap[path] = modified;
            }

            var sorted = pathMap
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList();

            var resultArray = new List<string>();
            int skipped = 0;

            foreach (string path in sorted)
            {
                if (resultArray.Count >= qty) break;

                if (!File.Exists(path)) continue;

                var txMatch = TxIdRegex.Match(path);
                if (!txMatch.Success) continue;
                string? address = ExtractAddressFromPath(path, txMatch.Index + txMatch.Length);
                if (string.IsNullOrEmpty(address)) continue;

                try
                {
                    string fileContent = await File.ReadAllTextAsync(path);
                    var jsonObj = JsonNode.Parse(fileContent)?.AsObject();
                    if (jsonObj == null) continue;

                    string detectedBlockchain = DetectBlockchain(address);

                    if (!MatchesBlockchainFilter(detectedBlockchain, blockchain)) continue;

                    if (skipped < skip)
                    {
                        skipped++;
                        continue;
                    }

                    jsonObj["Blockchain"] = detectedBlockchain;
                    resultArray.Add(jsonObj.ToJsonString());
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
                { /* Skip unreadable or malformed files */ }
            }

            string result = "[" + string.Join(",", resultArray) + "]";
            _cache.Set(cacheKey, result,
                new MemoryCacheEntryOptions { SlidingExpiration = CacheDuration });
            return result;
        }

        public async Task<string> SearchProfilesAsync(
            string searchString, int qty, int skip, string blockchain)
        {
            string cacheKey =
                $"profiles|{searchString.ToLowerInvariant()}|{qty}|{skip}|{blockchain.ToUpperInvariant()}";
            if (_cache.TryGetValue(cacheKey, out string? cached))
                return cached!;

            string sanitized = SanitizeSearchString(searchString);
            if (string.IsNullOrEmpty(sanitized))
                return "[]";

            var rawResults = await Task.Run(
                () => QuerySearchIndex(sanitized, "GetProfileByAddress.json"));

            // Deduplicate by file path, keeping newest modified date per path
            var pathMap = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, modified) in rawResults)
            {
                var match = TxIdRegex.Match(path);
                if (!match.Success) continue;
                string? address = ExtractAddressFromPath(path, match.Index + match.Length);
                if (string.IsNullOrEmpty(address)) continue;

                if (!pathMap.TryGetValue(path, out DateTime existing) || modified > existing)
                    pathMap[path] = modified;
            }

            var sorted = pathMap
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList();

            var resultArray = new List<string>();
            int skipped = 0;

            foreach (string path in sorted)
            {
                if (resultArray.Count >= qty) break;

                if (!File.Exists(path)) continue;

                var txMatch = TxIdRegex.Match(path);
                if (!txMatch.Success) continue;
                string? address = ExtractAddressFromPath(path, txMatch.Index + txMatch.Length);
                if (string.IsNullOrEmpty(address)) continue;

                try
                {
                    string fileContent = await File.ReadAllTextAsync(path);
                    var jsonObj = JsonNode.Parse(fileContent)?.AsObject();
                    if (jsonObj == null) continue;

                    string detectedBlockchain = DetectBlockchain(address);

                    if (!MatchesBlockchainFilter(detectedBlockchain, blockchain)) continue;

                    if (skipped < skip)
                    {
                        skipped++;
                        continue;
                    }

                    jsonObj["Blockchain"] = detectedBlockchain;
                    resultArray.Add(jsonObj.ToJsonString());
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
                { /* Skip unreadable or malformed files */ }
            }

            string result = "[" + string.Join(",", resultArray) + "]";
            _cache.Set(cacheKey, result,
                new MemoryCacheEntryOptions { SlidingExpiration = CacheDuration });
            return result;
        }
    }
}
