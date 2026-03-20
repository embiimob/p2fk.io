namespace P2FK.IO.Models
{
    /// <summary>
    /// Represents a profile (GetProfileByAddress.json) search result with blockchain detection.
    /// Each instance corresponds to the full profile JSON content with an injected Blockchain field.
    /// </summary>
    public class SearchResultProfile
    {
        public string? URN { get; set; }
        public string? Blockchain { get; set; }
    }
}
