using Microsoft.Extensions.Options;
using P2FK.IO.Models;
using P2FK.IO.Options;
using System.Net.Http.Headers;
using System.Text.Json;

namespace P2FK.IO.Services
{
    public class KuboIngressService : IKuboIngressService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        // Keep fallback imports in classic UnixFS/CIDv0-compatible mode so single-file cache migrations can
        // reproduce legacy Qm-style hashes when the selected file content matches the original object shape.
        private const string AddApiRelativeUrl = "/api/v0/add?pin=false&cid-version=0&hash=sha2-256&raw-leaves=false&wrap-with-directory=false";
        private readonly Uri _kuboApiBaseUri;
        private readonly Uri _kuboGatewayBaseUri;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IpfsIngressOptions _options;
        private readonly ILogger<KuboIngressService> _logger;

        public KuboIngressService(IHttpClientFactory httpClientFactory, IOptions<IpfsIngressOptions> options, ILogger<KuboIngressService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _logger = logger;
            _kuboApiBaseUri = BuildBaseUri(_options.KuboApiBaseUrl);
            _kuboGatewayBaseUri = BuildBaseUri(_options.KuboGatewayBaseUrl);
        }

        public async Task<KuboAddResult> AddAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
        {
            using var content = new MultipartFormDataContent();
            using var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "file", fileName);

            using var response = await CreateClient().PostAsync(BuildApiUri(AddApiRelativeUrl), content, cancellationToken);
            string payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Kubo add failed: {payload}");

            string jsonLine = payload
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault() ?? throw new InvalidOperationException("Kubo add returned an empty response.");

            var result = JsonSerializer.Deserialize<KuboAddResult>(jsonLine, JsonOptions)
                ?? throw new InvalidOperationException("Kubo add returned invalid JSON.");

            _logger.LogInformation("Kubo add completed for {FileName} with CID {Cid}", SanitizeForLog(fileName), result.Hash);
            return result;
        }

        public async Task FetchAsync(string cid, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildApiUri($"/api/v0/cat?arg={Uri.EscapeDataString(cid)}"));
            using var response = await CreateClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string payload = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Kubo fetch failed for CID {cid}: {payload}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await stream.CopyToAsync(Stream.Null, cancellationToken);
        }

        public Task PinAsync(string cid, CancellationToken cancellationToken = default) => PostNoContentAsync($"/api/v0/pin/add?arg={Uri.EscapeDataString(cid)}", cancellationToken);

        public Task UnpinAsync(string cid, CancellationToken cancellationToken = default) => PostNoContentAsync($"/api/v0/pin/rm?arg={Uri.EscapeDataString(cid)}", cancellationToken);

        public async Task<bool> IsPinnedAsync(string cid, CancellationToken cancellationToken = default)
        {
            TimeSpan lookupTimeout = TimeSpan.FromMilliseconds(Math.Max(100, _options.PinnedLookupTimeoutMilliseconds));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(lookupTimeout);

            HttpResponseMessage response;
            try
            {
                response = await CreateClient().PostAsync(
                    BuildApiUri($"/api/v0/pin/ls?arg={Uri.EscapeDataString(cid)}"),
                    content: null,
                    timeoutCts.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Kubo pin status timed out for CID {cid} after {lookupTimeout.TotalMilliseconds:0} ms.", ex);
            }

            using (response)
            {
                string payload;
                try
                {
                    payload = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                {
                    throw new InvalidOperationException($"Kubo pin status timed out for CID {cid} after {lookupTimeout.TotalMilliseconds:0} ms.", ex);
                }

                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(payload);
                    if (document.RootElement.TryGetProperty("Keys", out JsonElement keys)
                        && keys.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty property in keys.EnumerateObject())
                        {
                            if (string.Equals(property.Name, cid, StringComparison.Ordinal))
                                return true;
                        }
                    }

                    return false;
                }

                if (payload.Contains("not pinned", StringComparison.OrdinalIgnoreCase) ||
                    payload.Contains("no link named", StringComparison.OrdinalIgnoreCase) ||
                    payload.Contains("does not have pinned", StringComparison.OrdinalIgnoreCase))
                    return false;

                throw new InvalidOperationException($"Kubo pin status failed for CID {cid} with HTTP {(int)response.StatusCode}: {payload}");
            }

        }

        public async Task<long> GetRepoSizeAsync(CancellationToken cancellationToken = default)
        {
            using var response = await CreateClient().PostAsync(BuildApiUri("/api/v0/repo/stat?size-only=true"), content: null, cancellationToken);
            string payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Kubo repo stat failed: {payload}");

            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("RepoSize", out JsonElement repoSize)
                ? repoSize.GetInt64()
                : 0L;
        }

        public async Task RunGarbageCollectionAsync(CancellationToken cancellationToken = default)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

            using var response = await CreateClient().PostAsync(BuildApiUri("/api/v0/repo/gc"), content: null, timeoutCts.Token);
            string payload = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Kubo garbage collection failed: {payload}");
        }

        public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var versionResponse = await CreateClient().PostAsync(BuildApiUri("/api/v0/version"), content: null, cancellationToken);
                if (!versionResponse.IsSuccessStatusCode)
                    return false;

                using var statResponse = await CreateClient().PostAsync(BuildApiUri("/api/v0/repo/stat?size-only=true"), content: null, cancellationToken);
                return statResponse.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kubo ingress health check failed");
                return false;
            }
        }

        public Uri BuildGatewayUri(string cid, string? path)
        {
            string relativePath = string.IsNullOrWhiteSpace(path)
                ? $"/ipfs/{cid}"
                : $"/ipfs/{cid}/{path.TrimStart('/')}";
            return new Uri(_kuboGatewayBaseUri, relativePath.TrimStart('/'));
        }

        private async Task PostNoContentAsync(string relativeUrl, CancellationToken cancellationToken)
        {
            using var response = await CreateClient().PostAsync(BuildApiUri(relativeUrl), content: null, cancellationToken);
            string payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Kubo request failed for {relativeUrl}: {payload}");
        }

        private HttpClient CreateClient() => _httpClientFactory.CreateClient(nameof(KuboIngressService));

        private Uri BuildApiUri(string relativeUrl) => new(_kuboApiBaseUri, relativeUrl.TrimStart('/'));

        private static Uri BuildBaseUri(string configuredBaseUrl)
        {
            string baseUrl = configuredBaseUrl.Trim();
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var absoluteUri)
                && absoluteUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return EnsureTrailingSlash(new UriBuilder(absoluteUri) { Host = "127.0.0.1" }.Uri);
            }

            return EnsureTrailingSlash(new Uri(baseUrl));
        }

        private static Uri EnsureTrailingSlash(Uri uri) =>
            uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(uri.AbsoluteUri + "/");

        private static string SanitizeForLog(string value) =>
            value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
