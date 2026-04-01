namespace P2FK.IO.Models
{
    public class SearchResultProfile
    {
        public string Blockchain { get; set; } = "Unknown";
        public string Address { get; set; } = "";
        public object? Profile { get; set; }
    }
}
