using System.Text.Json.Serialization;

namespace P2FK.IO.Models
{
    public class KuboAddResult
    {
        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("Hash")]
        public string Hash { get; set; } = string.Empty;

        [JsonPropertyName("Size")]
        public string Size { get; set; } = string.Empty;
    }
}
