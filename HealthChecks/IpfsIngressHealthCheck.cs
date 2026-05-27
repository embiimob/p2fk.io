using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using P2FK.IO.Options;
using P2FK.IO.Services;

namespace P2FK.IO.HealthChecks
{
    public class IpfsIngressHealthCheck : IHealthCheck
    {
        private readonly IKuboIngressService _kuboIngressService;
        private readonly IpfsIngressOptions _options;

        public IpfsIngressHealthCheck(IKuboIngressService kuboIngressService, IOptions<IpfsIngressOptions> options)
        {
            _kuboIngressService = kuboIngressService;
            _options = options.Value;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            bool kuboConnected = await _kuboIngressService.IsHealthyAsync(cancellationToken);
            bool repoWritable = Directory.Exists(_options.RepoPath);
            bool healthy = kuboConnected && repoWritable;

            return healthy
                ? HealthCheckResult.Healthy("IPFS ingress healthy", new Dictionary<string, object> { ["kuboConnected"] = true })
                : HealthCheckResult.Unhealthy("IPFS ingress unhealthy", data: new Dictionary<string, object>
                {
                    ["kuboConnected"] = kuboConnected,
                    ["repoWritable"] = repoWritable
                });
        }
    }
}
