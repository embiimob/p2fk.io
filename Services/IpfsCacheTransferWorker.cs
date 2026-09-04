using Microsoft.Extensions.Options;
using P2FK.IO.Options;

namespace P2FK.IO.Services
{
    public sealed class IpfsCacheTransferWorker : BackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
        private const string TransferResultsFileName = "transfer-results.txt";

        private readonly IKuboIngressService _kuboIngressService;
        private readonly IpfsIngressOptions _options;
        private readonly ILogger<IpfsCacheTransferWorker> _logger;
        private readonly SemaphoreSlim _resultLogLock = new(1, 1);

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
            string transferResultsPath = Path.Combine(importPath, TransferResultsFileName);

            Directory.CreateDirectory(importPath);
            Directory.CreateDirectory(removePath);

            await ProcessImportsAsync(importPath, transferResultsPath, cancellationToken);
            await ProcessRemovalsAsync(removePath, transferResultsPath, cancellationToken);
        }

        private async Task ProcessImportsAsync(string importPath, string transferResultsPath, CancellationToken cancellationToken)
        {
            foreach (string cidFolderPath in Directory.EnumerateDirectories(importPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string cid = Path.GetFileName(cidFolderPath);
                if (string.IsNullOrWhiteSpace(cid))
                    continue;

                try
                {
                    bool imported = await TryFetchAndPinAsync(cid, transferResultsPath, cancellationToken);
                    if (!imported)
                        imported = await TryImportLargestFileAsync(cid, cidFolderPath, transferResultsPath, cancellationToken);

                    if (!imported)
                    {
                        await WriteTransferResultAsync(transferResultsPath, "IMPORT", cid, "FAILED", "no importable content was found for this folder", cancellationToken);
                        continue;
                    }

                    try
                    {
                        DeleteDirectoryIfExists(cidFolderPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "IPFS cache import succeeded but folder cleanup failed for {CidFolderPath}", cidFolderPath);
                        await WriteTransferResultAsync(transferResultsPath, "IMPORT", cid, "CLEANUP-FAILED", $"{cidFolderPath} :: {ex.Message}", cancellationToken);
                        continue;
                    }

                    _logger.LogInformation("IPFS cache import complete for folder {CidFolder}", cid);
                    await WriteTransferResultAsync(transferResultsPath, "IMPORT", cid, "SUCCESS", $"deleted source folder {cidFolderPath}", cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "IPFS cache import failed for folder {CidFolder}", cid);
                    await WriteTransferResultAsync(transferResultsPath, "IMPORT", cid, "FAILED", ex.Message, cancellationToken);
                }
            }
        }

        private async Task ProcessRemovalsAsync(string removePath, string transferResultsPath, CancellationToken cancellationToken)
        {
            var foldersAwaitingGc = new List<string>();

            foreach (string cidFolderPath in Directory.EnumerateDirectories(removePath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string cid = Path.GetFileName(cidFolderPath);
                if (string.IsNullOrWhiteSpace(cid))
                    continue;

                try
                {
                    bool isPinned = await _kuboIngressService.IsPinnedAsync(cid, cancellationToken);
                    if (!isPinned)
                    {
                        try
                        {
                            DeleteDirectoryIfExists(cidFolderPath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "IPFS cache removal succeeded but folder cleanup failed for {CidFolderPath}", cidFolderPath);
                            await WriteTransferResultAsync(transferResultsPath, "REMOVE", cid, "CLEANUP-FAILED", $"{cidFolderPath} :: {ex.Message}", cancellationToken);
                            continue;
                        }

                        _logger.LogInformation("IPFS cache removal complete for folder {CidFolder} pinned={WasPinned}", cid, isPinned);
                        await WriteTransferResultAsync(transferResultsPath, "REMOVE", cid, "SUCCESS", "CID was already absent; deleted marker folder", cancellationToken);
                        continue;
                    }

                    await _kuboIngressService.UnpinAsync(cid, cancellationToken);
                    foldersAwaitingGc.Add(cidFolderPath);
                    _logger.LogInformation("IPFS cache removal complete for folder {CidFolder} pinned={WasPinned}", cid, isPinned);
                    await WriteTransferResultAsync(transferResultsPath, "REMOVE", cid, "PENDING-GC", "CID was unpinned and folder is waiting for garbage collection", cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "IPFS cache removal failed for folder {CidFolder}", cid);
                    await WriteTransferResultAsync(transferResultsPath, "REMOVE", cid, "FAILED", ex.Message, cancellationToken);
                }
            }

            if (foldersAwaitingGc.Count == 0)
                return;

            await _kuboIngressService.RunGarbageCollectionAsync(cancellationToken);
            foreach (string path in foldersAwaitingGc)
            {
                string cid = Path.GetFileName(path);
                try
                {
                    DeleteDirectoryIfExists(path);
                    await WriteTransferResultAsync(transferResultsPath, "REMOVE", cid, "SUCCESS", "garbage collection completed and marker folder was deleted", cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "IPFS cache removal cleanup failed for {CidFolderPath} after garbage collection", path);
                    await WriteTransferResultAsync(transferResultsPath, "REMOVE", cid, "CLEANUP-FAILED", $"{path} :: {ex.Message}", cancellationToken);
                }
            }
        }

        private async Task<bool> TryFetchAndPinAsync(string cid, string transferResultsPath, CancellationToken cancellationToken)
        {
            try
            {
                await _kuboIngressService.FetchAsync(cid, cancellationToken);
                await _kuboIngressService.PinAsync(cid, cancellationToken);
                await WriteTransferResultAsync(transferResultsPath, "IMPORT", cid, "FETCHED", "CID was fetched from Kubo and pinned", cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                _logger.LogDebug(ex, "IPFS cache import fetch failed for CID {Cid}; falling back to local file import", cid);
                await WriteTransferResultAsync(transferResultsPath, "IMPORT", cid, "FETCH-MISS", "Kubo fetch failed; falling back to largest local file", cancellationToken);
                return false;
            }
        }

        private async Task<bool> TryImportLargestFileAsync(string requestedCid, string cidFolderPath, string transferResultsPath, CancellationToken cancellationToken)
        {
            FileInfo? largestFile = GetLargestFile(cidFolderPath);
            if (largestFile == null)
            {
                await WriteTransferResultAsync(transferResultsPath, "IMPORT", requestedCid, "NO-FILES", $"no files were found under {cidFolderPath}", cancellationToken);
                return false;
            }

            await using var stream = largestFile.OpenRead();
            var added = await _kuboIngressService.AddAsync(stream, largestFile.Name, cancellationToken);
            await _kuboIngressService.PinAsync(added.Hash, cancellationToken);

            if (!string.Equals(added.Hash, requestedCid, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "IPFS cache import fallback added file {FileName} as CID {ImportedCid} while processing requested folder CID {RequestedCid}",
                    largestFile.FullName,
                    added.Hash,
                    requestedCid);
                await WriteTransferResultAsync(transferResultsPath, "IMPORT", requestedCid, "IMPORTED-MISMATCH", $"{largestFile.FullName} -> {added.Hash}", cancellationToken);
            }
            else
            {
                _logger.LogInformation(
                    "IPFS cache import fallback added file {FileName} as CID {Cid}",
                    largestFile.FullName,
                    added.Hash);
                await WriteTransferResultAsync(transferResultsPath, "IMPORT", requestedCid, "IMPORTED", $"{largestFile.FullName} -> {added.Hash}", cancellationToken);
            }

            return true;
        }

        private static FileInfo? GetLargestFile(string cidFolderPath)
        {
            FileInfo? largest = null;
            foreach (FileInfo file in new DirectoryInfo(cidFolderPath).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (largest == null || file.Length > largest.Length)
                    largest = file;
            }

            return largest;
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            if (!Directory.Exists(path))
                return;

            ClearAttributesRecursive(path);
            Directory.Delete(path, recursive: true);
        }

        private static void ClearAttributesRecursive(string path)
        {
            var root = new DirectoryInfo(path);
            foreach (FileInfo file in root.EnumerateFiles("*", SearchOption.AllDirectories))
                file.Attributes = FileAttributes.Normal;

            foreach (DirectoryInfo directory in root.EnumerateDirectories("*", SearchOption.AllDirectories).OrderByDescending(directory => directory.FullName.Length))
                directory.Attributes = FileAttributes.Normal;

            root.Attributes = FileAttributes.Normal;
        }

        private async Task WriteTransferResultAsync(string transferResultsPath, string operation, string cid, string status, string detail, CancellationToken cancellationToken)
        {
            string line = $"{DateTimeOffset.UtcNow:O}\t{operation}\t{status}\t{cid}\t{detail.ReplaceLineEndings(" ")}{Environment.NewLine}";

            try
            {
                await _resultLogLock.WaitAsync(cancellationToken);
                try
                {
                    await File.AppendAllTextAsync(transferResultsPath, line, cancellationToken);
                }
                finally
                {
                    _resultLogLock.Release();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to append transfer results to {TransferResultsPath}", transferResultsPath);
            }
        }
    }
}
