namespace P2FK.IO.Models
{
    public class SearchResultRoot
    {
        public string Blockchain { get; set; } = "Unknown";
        public string TransactionId { get; set; } = "";
        public object? Root { get; set; }
    }
}
