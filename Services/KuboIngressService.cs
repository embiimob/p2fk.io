using P2FK.IO.Models;
using P2FK.IO.Options;
using System.Net.Http.Headers;
using System.Text.Json;

namespace P2FK.IO.Services
{
    public class KuboIngressService : IKuboIngressService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IpfsIngressOptions _options;
        private readonly ILogger<KuboIngressService> _logger;

        public KuboIngressService(IHttpClientFactory httpClientFactory, IOptions<IpfsIngressOptions> options, ILogger<KuboIngressService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<KuboAddResult> AddAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
        {
            using var content = new MultipartFormDataContent();
            using var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "file", fileName);

            using var response = await CreateClient().PostAsync(BuildApiUri("/api/v0/add?pin=false&cid-version=1&wrap-with-directory=false"), content, cancellationToken);
            string payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Kubo add failed: {payload}");

            string jsonLine = payload
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault() ?? throw new InvalidOperationException("Kubo add returned an empty response.");

            var result = JsonSerializer.Deserialize<KuboAddResult>(jsonLine, JsonOptions)
                ?? throw new InvalidOperationException("Kubo add returned invalid JSON.");

            _logger.LogInformation("Kubo add completed for {FileName} with CID {Cid}", fileName, result.Hash);
            return result;
        }

        public Task PinAsync(string cid, CancellationToken cancellationToken = default) => PostNoContentAsync($"/api/v0/pin/add?arg={Uri.EscapeDataString(cid)}", cancellationToken);

        public Task UnpinAsync(string cid, CancellationToken cancellationToken = default) => PostNoContentAsync($"/api/v0/pin/rm?arg={Uri.EscapeDataString(cid)}", cancellationToken);

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
            using var response = await CreateClient().PostAsync(BuildApiUri("/api/v0/repo/gc"), content: null, cancellationToken);
            string payload = await response.Content.ReadAsStringAsync(cancellationToken);
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
            return new Uri(new Uri(_options.KuboGatewayBaseUrl.TrimEnd('/') + "/"), relativePath.TrimStart('/'));
        }

        private async Task PostNoContentAsync(string relativeUrl, CancellationToken cancellationToken)
        {
            using var response = await CreateClient().PostAsync(BuildApiUri(relativeUrl), content: null, cancellationToken);
            string payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Kubo request failed for {relativeUrl}: {payload}");
        }

        private HttpClient CreateClient()
        {
            var client = _httpClientFactory.CreateClient(nameof(KuboIngressService));
            client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        }

        private Uri BuildApiUri(string relativeUrl) => new(new Uri(_options.KuboApiBaseUrl.TrimEnd('/') + "/"), relativeUrl.TrimStart('/'));
    }
}
