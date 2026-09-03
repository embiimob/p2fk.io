using Microsoft.Extensions.Options;
using P2FK.IO.Options;

namespace P2FK.IO.Services
{
    public sealed class IpfsCacheTransferWorker : BackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

        private readonly IKuboIngressService _kuboIngressService;
        private readonly IpfsIngressOptions _options;
        private readonly ILogger<IpfsCacheTransferWorker> _logger;

        public IpfsCacheTransferWorker(
            IKuboIngressService kuboIngressService,
            IOptions<IpfsIngressOptions> options,
            ILogger<IpfsCacheTransferWorker> logger)
        {
            _kuboIngressService = kuboIngressService;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(StartupDelay, stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await ProcessTransferFoldersAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "IPFS cache transfer processing failed");
                    }

                    await Task.Delay(TimeSpan.FromMinutes(_options.CleanupIntervalMinutes), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task ProcessTransferFoldersAsync(CancellationToken cancellationToken)
        {
            string importPath = Path.Combine(_options.RepoPath, "import");
            string removePath = Path.Combine(_options.RepoPath, "remove");

            Directory.CreateDirectory(importPath);
            Directory.CreateDirectory(removePath);

            await ProcessImportsAsync(importPath, cancellationToken);
            await ProcessRemovalsAsync(removePath, cancellationToken);
        }

        private async Task ProcessImportsAsync(string importPath, CancellationToken cancellationToken)
        {
            foreach (string cidFolderPath in Directory.EnumerateDirectories(importPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string cid = Path.GetFileName(cidFolderPath);
                if (string.IsNullOrWhiteSpace(cid))
                    continue;

                try
                {
                    bool imported = await TryFetchAndPinAsync(cid, cancellationToken);
                    if (!imported)
                        imported = await TryImportLargestFileAsync(cidFolderPath, cancellationToken);

                    if (!imported)
                        continue;

                    DeleteDirectoryIfExists(cidFolderPath);
                    _logger.LogInformation("IPFS cache import complete for folder {CidFolder}", cid);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "IPFS cache import failed for folder {CidFolder}", cid);
                }
            }
        }

        private async Task ProcessRemovalsAsync(string removePath, CancellationToken cancellationToken)
        {
            bool removedAny = false;

            foreach (string cidFolderPath in Directory.EnumerateDirectories(removePath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string cid = Path.GetFileName(cidFolderPath);
                if (string.IsNullOrWhiteSpace(cid))
                    continue;

                try
                {
                    bool isPinned = await _kuboIngressService.IsPinnedAsync(cid, cancellationToken);
                    if (isPinned)
                        await _kuboIngressService.UnpinAsync(cid, cancellationToken);

                    DeleteDirectoryIfExists(cidFolderPath);
                    removedAny = removedAny || isPinned;
                    _logger.LogInformation("IPFS cache removal complete for folder {CidFolder} pinned={WasPinned}", cid, isPinned);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "IPFS cache removal failed for folder {CidFolder}", cid);
                }
            }

            if (removedAny)
                await _kuboIngressService.RunGarbageCollectionAsync(cancellationToken);
        }

        private async Task<bool> TryFetchAndPinAsync(string cid, CancellationToken cancellationToken)
        {
            try
            {
                await _kuboIngressService.FetchAsync(cid, cancellationToken);
                await _kuboIngressService.PinAsync(cid, cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                _logger.LogDebug(ex, "IPFS cache import fetch failed for CID {Cid}; falling back to local file import", cid);
                return false;
            }
        }

        private async Task<bool> TryImportLargestFileAsync(string cidFolderPath, CancellationToken cancellationToken)
        {
            FileInfo? largestFile = GetLargestFile(cidFolderPath);
            if (largestFile == null)
                return false;

            await using var stream = largestFile.OpenRead();
            KuboAddResult added = await _kuboIngressService.AddAsync(stream, largestFile.Name, cancellationToken);
            await _kuboIngressService.PinAsync(added.Hash, cancellationToken);

            _logger.LogInformation(
                "IPFS cache import fallback added file {FileName} as CID {Cid}",
                largestFile.FullName,
                added.Hash);

            return true;
        }

        private static FileInfo? GetLargestFile(string cidFolderPath) =>
            new DirectoryInfo(cidFolderPath)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .OrderByDescending(file => file.Length)
                .FirstOrDefault();

        private static void DeleteDirectoryIfExists(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }
}
