using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using P2FK.IO.Options;
using P2FK.IO.Services;
using System.IO.Compression;
using System.Net.Mime;

namespace P2FK.IO.Controllers
{
    [ApiController]
    public sealed class IpfsCacheAdminController : ControllerBase
    {
        private static readonly object QueueMutationSync = new();
        private readonly IKuboIngressService _kuboIngressService;
        private readonly IngressMetadataStore _metadataStore;
        private readonly string _repoPath;
        private readonly ILogger<IpfsCacheAdminController> _logger;

        public IpfsCacheAdminController(
            IKuboIngressService kuboIngressService,
            IngressMetadataStore metadataStore,
            IOptions<IpfsIngressOptions> options,
            ILogger<IpfsCacheAdminController> logger)
        {
            _kuboIngressService = kuboIngressService;
            _metadataStore = metadataStore;
            _repoPath = options.Value.RepoPath;
            _logger = logger;
        }

        /// <summary>Exports every currently recursive-pinned Kubo CID as an empty CID-named folder inside a zip file. Only shown in Swagger when browsing via 127.0.0.1.</summary>
        [HttpGet("ipfs/cache-admin/export")]
        [LocalhostOnly]
        [Produces("application/zip")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> ExportPinnedCidFolders(CancellationToken cancellationToken)
        {
            IReadOnlyList<string> cids = await _kuboIngressService.ListPinnedCidsAsync(cancellationToken);

            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "application/zip";
            Response.Headers.ContentDisposition = new ContentDisposition
            {
                FileName = $"ipfs-pinned-cids-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.zip",
                Inline = false
            }.ToString();

            using (var archive = new ZipArchive(Response.Body, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (string cid in cids)
                    archive.CreateEntry($"{cid.TrimEnd('/')}/");
            }

            await Response.Body.FlushAsync(cancellationToken);
            _logger.LogInformation("Exported {PinnedCidCount} pinned IPFS CIDs as a folder-only zip archive", cids.Count);
            return new EmptyResult();
        }

        /// <summary>Queues a single CID to become part of the always-pinned IPFS cache. Only shown in Swagger when browsing via 127.0.0.1.</summary>
        [HttpPost("ipfs/cache-admin/pins/{cid}")]
        [LocalhostOnly]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> AddPinnedCid(string cid, CancellationToken cancellationToken)
        {
            if (!TryNormalizeCid(cid, out string normalizedCid))
                return BadRequest(new { error = "CID is required and must be a single folder-safe name." });

            if (await _metadataStore.IsCidActiveAsync(normalizedCid, DateTimeOffset.UtcNow, cancellationToken))
            {
                await _metadataStore.MarkCidAsNonExpiringAsync(normalizedCid, cancellationToken);
                return Ok(new { cid = normalizedCid, status = "promoted-to-always-pinned" });
            }

            bool isPinned = false;
            try
            {
                isPinned = await _kuboIngressService.IsPinnedAsync(normalizedCid, cancellationToken);
            }
            catch (KuboPinStatusTimeoutException ex)
            {
                _logger.LogWarning(ex, "Timed out checking pinned status for CID {Cid}; queueing add request instead", SanitizeForLog(normalizedCid));
            }

            if (isPinned)
                return Ok(new { cid = normalizedCid, status = "already-pinned" });

            string importPath = Path.Combine(_repoPath, "import");
            string removePath = Path.Combine(_repoPath, "remove");
            return QueueOperation(
                normalizedCid,
                importPath,
                removePath,
                "queued-for-import");
        }

        /// <summary>Queues a single CID for removal from the always-pinned IPFS cache. Only shown in Swagger when browsing via 127.0.0.1.</summary>
        [HttpDelete("ipfs/cache-admin/pins/{cid}")]
        [LocalhostOnly]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> RemovePinnedCid(string cid, CancellationToken cancellationToken)
        {
            if (!TryNormalizeCid(cid, out string normalizedCid))
                return BadRequest(new { error = "CID is required and must be a single folder-safe name." });

            if (await _metadataStore.IsCidActiveAsync(normalizedCid, DateTimeOffset.UtcNow, cancellationToken))
                return Conflict(new { error = "CID is still in the temporary ingress queue and cannot be removed from the always-pinned cache until it expires or is promoted first." });

            bool? isPinned = null;
            try
            {
                isPinned = await _kuboIngressService.IsPinnedAsync(normalizedCid, cancellationToken);
            }
            catch (KuboPinStatusTimeoutException ex)
            {
                _logger.LogWarning(ex, "Timed out checking pinned status for CID {Cid}; queueing removal request instead", SanitizeForLog(normalizedCid));
            }

            string importPath = Path.Combine(_repoPath, "import");
            string removePath = Path.Combine(_repoPath, "remove");

            if (isPinned == false)
                return Ok(new { cid = normalizedCid, status = "not-pinned" });

            return QueueOperation(
                normalizedCid,
                removePath,
                importPath,
                "queued-for-removal");
        }

        private static bool TryNormalizeCid(string cid, out string normalizedCid)
        {
            normalizedCid = cid.Trim();
            return !string.IsNullOrWhiteSpace(normalizedCid)
                && normalizedCid is not "." and not ".."
                && normalizedCid.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && !normalizedCid.Contains(Path.DirectorySeparatorChar)
                && !normalizedCid.Contains(Path.AltDirectorySeparatorChar);
        }

        private string? TryClearEmptyConflictFolder(string rootPath, string cid)
        {
            string conflictPath = Path.Combine(rootPath, cid);
            if (System.IO.File.Exists(conflictPath))
                return $"Conflicting queued file already exists at {conflictPath}. Clear it manually before issuing the opposite cache operation.";
            if (!Directory.Exists(conflictPath))
                return null;

            if (Directory.EnumerateFileSystemEntries(conflictPath).Any())
                return $"Conflicting queued folder already exists at {conflictPath}. Clear it manually before issuing the opposite cache operation.";

            Directory.Delete(conflictPath, recursive: true);
            return null;
        }

        private IActionResult QueueOperation(string cid, string targetRootPath, string conflictRootPath, string queuedStatus)
        {
            lock (QueueMutationSync)
            {
                string? conflict = TryClearEmptyConflictFolder(conflictRootPath, cid);
                if (conflict is not null)
                    return Conflict(new { error = conflict });

                string targetPath = Path.Combine(targetRootPath, cid);
                if (System.IO.File.Exists(targetPath))
                    return Conflict(new { error = $"A queued file already exists at {targetPath}. Clear it manually before retrying." });

                Directory.CreateDirectory(targetPath);
            }

            return Accepted(new { cid, status = queuedStatus });
        }

        private static string SanitizeForLog(string value) =>
            value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    internal sealed class LocalhostOnlyAttribute : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            if (LocalIpfsAdminAccess.IsLoopbackRequest(context.HttpContext))
                return;

            context.Result = new NotFoundObjectResult(new { error = "Endpoint is only available from localhost" });
        }
    }
}
