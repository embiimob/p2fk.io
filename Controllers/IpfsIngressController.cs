using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using P2FK.IO.Models;
using P2FK.IO.Options;
using P2FK.IO.Services;
using System.Text;

namespace P2FK.IO.Controllers
{
    [ApiController]
    public class IpfsIngressController : ControllerBase
    {
        private readonly IpfsIngressService _ipfsIngressService;
        private readonly IngressMetadataStore _metadataStore;
        private readonly KuboIngressService _kuboIngressGatewayService;
        private readonly IpfsIngressOptions _options;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<IpfsIngressController> _logger;

        public IpfsIngressController(
            IpfsIngressService ipfsIngressService,
            IngressMetadataStore metadataStore,
            IKuboIngressService kuboIngressGatewayService,
            IOptions<IpfsIngressOptions> options,
            IHttpClientFactory httpClientFactory,
            ILogger<IpfsIngressController> logger)
        {
            _ipfsIngressService = ipfsIngressService;
            _metadataStore = metadataStore;
            _kuboIngressGatewayService = (KuboIngressService)kuboIngressGatewayService;
            _options = options.Value;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>Streams a file into the temporary ingress Kubo node and returns a Kubo-style add result.</summary>
        [HttpPost("api/v0/add")]
        [DisableRequestTimeout]
        [EnableRateLimiting("IpfsUpload")]
        [Consumes("multipart/form-data", "application/octet-stream")]
        public async Task<ActionResult<KuboAddResult>> Add(CancellationToken cancellationToken)
        {
            return await HandleUploadAsync(
                async upload => new JsonResult(new KuboAddResult
                {
                    Name = upload.AddResult.Name,
                    Hash = upload.AddResult.Hash,
                    Size = upload.AddResult.Size
                }),
                cancellationToken);
        }

        /// <summary>Streams a file into the temporary ingress Kubo node and returns ingress metadata.</summary>
        [HttpPost("ipfs")]
        [DisableRequestTimeout]
        [EnableRateLimiting("IpfsUpload")]
        [Consumes("multipart/form-data", "application/octet-stream")]
        public async Task<ActionResult> Upload(CancellationToken cancellationToken)
        {
            return await HandleUploadAsync(
                upload => Task.FromResult<ActionResult>(new JsonResult(new
                {
                    cid = upload.AddResult.Hash,
                    gatewayUrl = upload.GatewayUrl,
                    expiresUtc = upload.ExpiresUtc
                })),
                cancellationToken);
        }

        /// <summary>Returns public status information for the temporary IPFS ingress node.</summary>
        [HttpGet("ipfs/status")]
        public async Task<ActionResult<IpfsStatusResponse>> Status(CancellationToken cancellationToken)
        {
            var status = await _ipfsIngressService.GetStatusAsync(cancellationToken);
            return new JsonResult(status);
        }

        /// <summary>Lists currently active ingress uploads and their expiration times.</summary>
        [HttpGet("ipfs/queue")]
        public async Task<ActionResult<IReadOnlyList<IpfsQueueItemResponse>>> Queue(CancellationToken cancellationToken)
        {
            var queue = await _ipfsIngressService.GetQueueAsync(cancellationToken);
            return new JsonResult(queue);
        }

        /// <summary>Optionally proxies active ingress content through the local Kubo gateway while it remains pinned.</summary>
        [HttpGet("ipfs/{cid}/{**path}")]
        public async Task<IActionResult> Gateway(string cid, string? path, CancellationToken cancellationToken)
        {
            if (!await _metadataStore.IsCidActiveAsync(cid, DateTimeOffset.UtcNow, cancellationToken))
                return NotFound(new { error = "CID not currently available via ingress gateway" });

            using var request = new HttpRequestMessage(HttpMethod.Get, _kuboIngressGatewayService.BuildGatewayUri(cid, path));
            using var response = await _httpClientFactory.CreateClient(nameof(KuboIngressService))
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, new { error = "Local Kubo gateway could not serve the requested CID" });

            Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is not null)
                Response.ContentType = response.Content.Headers.ContentType.ToString();
            if (response.Content.Headers.ContentLength.HasValue)
                Response.ContentLength = response.Content.Headers.ContentLength.Value;
            if (response.Content.Headers.LastModified.HasValue)
                Response.Headers.LastModified = response.Content.Headers.LastModified.Value.ToString("R");
            if (response.Headers.TryGetValues("Accept-Ranges", out var acceptRanges))
                Response.Headers.Append("Accept-Ranges", acceptRanges.ToArray());

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await stream.CopyToAsync(Response.Body, cancellationToken);
            return new EmptyResult();
        }

        private async Task<ActionResult> HandleUploadAsync(Func<IngressUploadResult, Task<ActionResult>> projector, CancellationToken cancellationToken)
        {
            try
            {
                var uploadRequest = await ReadUploadRequestAsync(cancellationToken);
                string clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var upload = await _ipfsIngressService.UploadAsync(
                    uploadRequest.Stream,
                    uploadRequest.FileName,
                    clientIp,
                    uploadRequest.ContentLength,
                    cancellationToken);

                return await projector(upload);
            }
            catch (DailyUploadQuotaExceededException)
            {
                _logger.LogWarning("Ingress upload rejected due to daily quota. clientIp={ClientIp}", HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Daily upload quota exceeded" });
            }
            catch (TemporaryIngressCacheFullException)
            {
                _logger.LogWarning("Ingress upload rejected because the temporary cache is full. clientIp={ClientIp}", HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                return StatusCode(StatusCodes.Status507InsufficientStorage, new { error = "Temporary ingress cache full" });
            }
            catch (InvalidDataException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (NotSupportedException ex)
            {
                return StatusCode(StatusCodes.Status415UnsupportedMediaType, new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Ingress upload failed while talking to Kubo");
                return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
            }
        }

        private async Task<UploadRequest> ReadUploadRequestAsync(CancellationToken cancellationToken)
        {
            string contentType = Request.ContentType ?? string.Empty;
            if (MultipartRequestHelper.IsMultipartContentType(contentType))
                return await ReadMultipartUploadRequestAsync(contentType, cancellationToken);

            if (Request.Body == Stream.Null)
                throw new InvalidDataException("No upload body was provided.");

            string fileName = Request.Headers.TryGetValue("X-File-Name", out var headerFileName)
                ? headerFileName.ToString()
                : Request.Query["filename"].ToString();

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "upload.bin";

            return new UploadRequest(Request.Body, fileName, Request.ContentLength);
        }

        private async Task<UploadRequest> ReadMultipartUploadRequestAsync(string contentType, CancellationToken cancellationToken)
        {
            string boundary = MultipartRequestHelper.GetBoundary(MediaTypeHeaderValue.Parse(contentType), lengthLimit: 256);
            var reader = new MultipartReader(boundary, Request.Body);
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var contentDisposition))
                    continue;
                if (!MultipartRequestHelper.HasFileContentDisposition(contentDisposition))
                    continue;

                string fileName = contentDisposition.FileNameStar.Value ?? contentDisposition.FileName.Value ?? "upload.bin";
                fileName = fileName.Trim('"');
                long? sectionLength = Request.ContentLength;
                if (section.Headers is not null
                    && section.Headers.TryGetValue(HeaderNames.ContentLength, out var headerLength)
                    && long.TryParse(headerLength.ToString(), out long parsedLength))
                    sectionLength = parsedLength;
                return new UploadRequest(section.Body, fileName, sectionLength);
            }

            throw new InvalidDataException("No file section was found in the multipart payload.");
        }

        private sealed record UploadRequest(Stream Stream, string FileName, long? ContentLength);
    }

    internal static class MultipartRequestHelper
    {
        public static string GetBoundary(MediaTypeHeaderValue contentType, int lengthLimit)
        {
            string? boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
            if (string.IsNullOrWhiteSpace(boundary))
                throw new InvalidDataException("Missing multipart boundary.");
            if (boundary.Length > lengthLimit)
                throw new InvalidDataException($"Multipart boundary length limit {lengthLimit} exceeded.");
            return boundary;
        }

        public static bool IsMultipartContentType(string? contentType) =>
            !string.IsNullOrWhiteSpace(contentType) && contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase);

        public static bool HasFileContentDisposition(ContentDispositionHeaderValue contentDisposition) =>
            contentDisposition.DispositionType.Equals("form-data")
            && (!string.IsNullOrEmpty(contentDisposition.FileName.Value)
                || !string.IsNullOrEmpty(contentDisposition.FileNameStar.Value));
    }

}
