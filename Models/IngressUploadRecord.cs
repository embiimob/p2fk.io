namespace P2FK.IO.Models
{
    public class IngressUploadRecord
    {
        public Guid Id { get; set; }
        public string CID { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public string ClientIp { get; set; } = string.Empty;
        public DateTimeOffset UploadedUtc { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }
        public bool IsPinned { get; set; }
        public bool IsExpired { get; set; }
    }
}
