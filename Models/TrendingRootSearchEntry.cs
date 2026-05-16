namespace P2FK.IO.Models
{
    public class TrendingRootSearchEntry
    {
        public int Rank { get; set; }
        public string SearchString { get; set; } = "";
        public int SuccessfulSearchCount { get; set; }
        public int LastResultCount { get; set; }
        public double AverageResultCount { get; set; }
        public int MaxResultCount { get; set; }
        public DateTimeOffset LastSearchedAtUtc { get; set; }
        public double Score { get; set; }
    }
}
