namespace P2FK.IO.Models
{
    public class IpfsQueueItemResponse
    {
        public string FileName { get; set; } = string.Empty;
        public string Cid { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public DateTimeOffset UploadedUtc { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }
        public int MinutesRemaining { get; set; }
    }
}
