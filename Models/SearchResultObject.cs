namespace P2FK.IO.Models
{
    /// <summary>
    /// Represents an object (OBJ.json) search result with blockchain detection.
    /// Each instance corresponds to the full OBJ.json content with an injected Blockchain field.
    /// </summary>
    public class SearchResultObject
    {
        public string? TransactionId { get; set; }
        public string? Blockchain { get; set; }
    }
}
