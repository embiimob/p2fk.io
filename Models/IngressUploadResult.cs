namespace P2FK.IO.Models
{
    public class IngressUploadResult
    {
        public KuboAddResult AddResult { get; set; } = new();
        public DateTimeOffset ExpiresUtc { get; set; }
        public string GatewayUrl { get; set; } = string.Empty;
    }
}
