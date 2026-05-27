using P2FK.IO.Options;

namespace P2FK.IO.Services
{
    public class IngressExpirationWorker : BackgroundService
    {
        private readonly IngressMetadataStore _metadataStore;
        private readonly IKuboIngressService _kuboIngressService;
        private readonly IpfsIngressOptions _options;
        private readonly ILogger<IngressExpirationWorker> _logger;

        public IngressExpirationWorker(
            IngressMetadataStore metadataStore,
            IKuboIngressService kuboIngressService,
            IOptions<IpfsIngressOptions> options,
            ILogger<IngressExpirationWorker> logger)
        {
            _metadataStore = metadataStore;
            _kuboIngressService = kuboIngressService;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CleanupExpiredUploadsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Ingress expiration cleanup failed");
                }

                await Task.Delay(TimeSpan.FromMinutes(_options.CleanupIntervalMinutes), stoppingToken);
            }
        }

        private async Task CleanupExpiredUploadsAsync(CancellationToken cancellationToken)
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            var expiredUploads = await _metadataStore.GetExpiredUploadsAsync(nowUtc, cancellationToken);
            if (expiredUploads.Count == 0)
                return;

            long reclaimedBytes = 0;
            int expiredCount = 0;

            foreach (var upload in expiredUploads)
            {
                try
                {
                    await _kuboIngressService.UnpinAsync(upload.CID, cancellationToken);
                    await _metadataStore.MarkExpiredAsync(upload.Id, cancellationToken);
                    reclaimedBytes += upload.FileSizeBytes;
                    expiredCount++;

                    _logger.LogInformation(
                        "Ingress upload expired CID={Cid} fileSizeBytes={FileSizeBytes} clientIp={ClientIp} expiresUtc={ExpiresUtc}",
                        upload.CID,
                        upload.FileSizeBytes,
                        upload.ClientIp,
                        upload.ExpiresUtc);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to expire ingress upload CID={Cid}", upload.CID);
                }
            }

            if (expiredCount == 0)
                return;

            await _kuboIngressService.RunGarbageCollectionAsync(cancellationToken);
            _logger.LogInformation(
                "Ingress cleanup complete expiredCount={ExpiredCount} reclaimedBytes={ReclaimedBytes}",
                expiredCount,
                reclaimedBytes);
        }
    }
}
