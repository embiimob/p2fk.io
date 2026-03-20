namespace P2FK.IO.Models
{
    /// <summary>
    /// Represents a root (root.json) search result with blockchain detection.
    /// Each instance corresponds to the full root.json content with an injected Blockchain field.
    /// </summary>
    public class SearchResultRoot
    {
        public string? TransactionId { get; set; }
        public string? Blockchain { get; set; }
    }
}
