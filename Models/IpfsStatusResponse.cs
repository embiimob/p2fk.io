namespace P2FK.IO.Models
{
    public class IpfsStatusResponse
    {
        public bool KuboConnected { get; set; }
        public long RepoSizeBytes { get; set; }
        public long MaxCacheBytes { get; set; }
        public int ActivePins { get; set; }
        public long QueuedBytes { get; set; }
        public DateTimeOffset? OldestExpirationUtc { get; set; }
    }
}
