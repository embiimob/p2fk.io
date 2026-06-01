namespace P2FK.IO.Options
{
    public class IpfsIngressOptions
    {
        public const string SectionName = "IpfsIngress";
        public const long DefaultMaxUploadBytes = 524_288_000;

        public string PublicBaseUrl { get; set; } = "https://p2fk.io";
        public bool ManageKuboProcess { get; set; } = true;
        public string KuboExecutablePath { get; set; } = OperatingSystem.IsWindows() ? @"tools\kubo\kubo.exe" : "kubo";
        public string KuboInitProfile { get; set; } = "server";
        public string KuboApiBaseUrl { get; set; } = "http://127.0.0.1:5101";
        public string KuboGatewayBaseUrl { get; set; } = "http://127.0.0.1:8180";
        public string KuboApiMultiAddress { get; set; } = "/ip4/127.0.0.1/tcp/5101";
        public string KuboGatewayMultiAddress { get; set; } = "/ip4/127.0.0.1/tcp/8180";
        public string[] KuboSwarmMultiAddresses { get; set; } = ["/ip4/0.0.0.0/tcp/4101", "/ip6/::/tcp/4101"];
        public int KuboStartupTimeoutSeconds { get; set; } = 30;
        public string RepoPath { get; set; } = @"D:\SupIngress";
        public string DatabasePath { get; set; } = "App_Data/ipfs-ingress.db";
        public long MaxActiveCacheBytes { get; set; } = 536_870_912_000;
        public long DailyIpQuotaBytes { get; set; } = 5_368_709_120;
        public int PinLifetimeMinutes { get; set; } = 60;
        public int CleanupIntervalMinutes { get; set; } = 5;
        public int UploadRequestsPerMinute { get; set; } = 20;
        public long MaxUploadBytes { get; set; } = DefaultMaxUploadBytes;
    }
}
