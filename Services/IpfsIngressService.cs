using Microsoft.Extensions.Options;
using P2FK.IO.Models;
using P2FK.IO.Options;

namespace P2FK.IO.Services
{
    public class IpfsIngressService
    {
        private readonly object _reservationSync = new();
        private readonly Dictionary<string, long> _pendingBytesByIp = new(StringComparer.OrdinalIgnoreCase);
        private long _pendingBytesTotal;
        private readonly IKuboIngressService _kuboIngressService;
        private readonly IngressMetadataStore _metadataStore;
        private readonly IpfsIngressOptions _options;
        private readonly ILogger<IpfsIngressService> _logger;

        public IpfsIngressService(
            IKuboIngressService kuboIngressService,
            IngressMetadataStore metadataStore,
            IOptions<IpfsIngressOptions> options,
            ILogger<IpfsIngressService> logger)
        {
            _kuboIngressService = kuboIngressService;
            _metadataStore = metadataStore;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<IngressUploadResult> UploadAsync(Stream stream, string fileName, string clientIp, long? contentLength, CancellationToken cancellationToken = default)
        {
            if (contentLength is null || contentLength <= 0)
                throw new InvalidDataException("A valid Content-Length header is required for ingress uploads.");

            long estimatedBytes = contentLength.Value;
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            await ReserveCapacityAsync(clientIp, estimatedBytes, nowUtc, cancellationToken);

            KuboAddResult? addResult = null;
            try
            {
                addResult = await _kuboIngressService.AddAsync(stream, fileName, cancellationToken);
                await _kuboIngressService.PinAsync(addResult.Hash, cancellationToken);

                long actualBytes = long.TryParse(addResult.Size, out long parsedSize) ? parsedSize : estimatedBytes;
                DateTimeOffset expiresUtc = nowUtc.AddMinutes(_options.PinLifetimeMinutes);

                var record = new IngressUploadRecord
                {
                    Id = Guid.NewGuid(),
                    CID = addResult.Hash,
                    FileName = string.IsNullOrWhiteSpace(fileName) ? addResult.Hash : fileName,
                    FileSizeBytes = actualBytes,
                    ClientIp = clientIp,
                    UploadedUtc = nowUtc,
                    ExpiresUtc = expiresUtc,
                    IsPinned = true,
                    IsExpired = false
                };

                await _metadataStore.RecordUploadAsync(record, cancellationToken);

                _logger.LogInformation(
                    "Ingress upload stored CID={Cid} fileName={FileName} fileSizeBytes={FileSizeBytes} clientIp={ClientIp} expiresUtc={ExpiresUtc}",
                    record.CID,
                    record.FileName,
                    record.FileSizeBytes,
                    record.ClientIp,
                    record.ExpiresUtc);

                return new IngressUploadResult
                {
                    AddResult = new KuboAddResult
                    {
                        Name = record.FileName,
                        Hash = record.CID,
                        Size = record.FileSizeBytes.ToString()
                    },
                    ExpiresUtc = expiresUtc,
                    GatewayUrl = BuildGatewayUrl(record.CID)
                };
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(addResult?.Hash))
                {
                    try
                    {
                        await _kuboIngressService.UnpinAsync(addResult.Hash, cancellationToken);
                        await _kuboIngressService.RunGarbageCollectionAsync(cancellationToken);
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogWarning(cleanupEx, "Failed to rollback ingress upload for CID {Cid}", addResult.Hash);
                    }
                }

                throw;
            }
            finally
            {
                ReleaseReservation(clientIp, estimatedBytes);
            }
        }

        public async Task<IpfsStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            var activeStatus = await _metadataStore.GetActiveStatusAsync(nowUtc, cancellationToken);
            bool kuboConnected = await _kuboIngressService.IsHealthyAsync(cancellationToken);
            long repoSizeBytes = 0;

            if (kuboConnected)
            {
                try
                {
                    repoSizeBytes = await _kuboIngressService.GetRepoSizeAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    kuboConnected = false;
                    _logger.LogWarning(ex, "Failed to query ingress repo size");
                }
            }

            return new IpfsStatusResponse
            {
                KuboConnected = kuboConnected,
                RepoSizeBytes = repoSizeBytes,
                MaxCacheBytes = _options.MaxActiveCacheBytes,
                ActivePins = activeStatus.ActivePins,
                QueuedBytes = activeStatus.QueuedBytes,
                OldestExpirationUtc = activeStatus.OldestExpirationUtc
            };
        }

        public async Task<IReadOnlyList<IpfsQueueItemResponse>> GetQueueAsync(CancellationToken cancellationToken = default)
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            var activeUploads = await _metadataStore.GetActiveUploadsAsync(nowUtc, cancellationToken);
            return activeUploads
                .Select(upload => new IpfsQueueItemResponse
                {
                    FileName = upload.FileName,
                    Cid = upload.CID,
                    SizeBytes = upload.FileSizeBytes,
                    UploadedUtc = upload.UploadedUtc,
                    ExpiresUtc = upload.ExpiresUtc,
                    MinutesRemaining = Math.Max(0, (int)Math.Ceiling((upload.ExpiresUtc - nowUtc).TotalMinutes))
                })
                .ToList();
        }

        public Task<bool> IsCidActiveAsync(string cid, CancellationToken cancellationToken = default) =>
            _metadataStore.IsCidActiveAsync(cid, DateTimeOffset.UtcNow, cancellationToken);

        private async Task ReserveCapacityAsync(string clientIp, long estimatedBytes, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        {
            long rollingUsageBytes = await _metadataStore.GetRollingUsageBytesAsync(clientIp, nowUtc.AddHours(-24), cancellationToken);
            long repoSizeBytes = await _kuboIngressService.GetRepoSizeAsync(cancellationToken);

            lock (_reservationSync)
            {
                long pendingForIp = _pendingBytesByIp.TryGetValue(clientIp, out long reserved) ? reserved : 0;
                if (rollingUsageBytes + pendingForIp + estimatedBytes > _options.DailyIpQuotaBytes)
                    throw new DailyUploadQuotaExceededException("Daily upload quota exceeded");

                if (repoSizeBytes + _pendingBytesTotal + estimatedBytes > _options.MaxActiveCacheBytes)
                    throw new TemporaryIngressCacheFullException("Temporary ingress cache full");

                _pendingBytesTotal += estimatedBytes;
                _pendingBytesByIp[clientIp] = pendingForIp + estimatedBytes;
            }
        }

        private void ReleaseReservation(string clientIp, long reservedBytes)
        {
            lock (_reservationSync)
            {
                _pendingBytesTotal = Math.Max(0, _pendingBytesTotal - reservedBytes);
                if (!_pendingBytesByIp.TryGetValue(clientIp, out long current))
                    return;

                long remaining = current - reservedBytes;
                if (remaining > 0)
                    _pendingBytesByIp[clientIp] = remaining;
                else
                    _pendingBytesByIp.Remove(clientIp);
            }
        }

        private string BuildGatewayUrl(string cid) => $"{_options.PublicBaseUrl.TrimEnd('/')}/ipfs/{cid}";
    }
}
