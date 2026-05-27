namespace P2FK.IO.Options
{
    public class IpfsIngressOptions
    {
        public const string SectionName = "IpfsIngress";

        public string PublicBaseUrl { get; set; } = "https://p2fk.io";
        public string KuboApiBaseUrl { get; set; } = "http://127.0.0.1:5101";
        public string KuboGatewayBaseUrl { get; set; } = "http://127.0.0.1:8180";
        public string RepoPath { get; set; } = @"D:\SupIngress";
        public string DatabasePath { get; set; } = "App_Data/ipfs-ingress.db";
        public long MaxActiveCacheBytes { get; set; } = 536_870_912_000;
        public long DailyIpQuotaBytes { get; set; } = 5_368_709_120;
        public int PinLifetimeMinutes { get; set; } = 60;
        public int CleanupIntervalMinutes { get; set; } = 5;
        public int UploadRequestsPerMinute { get; set; } = 20;
    }
}
