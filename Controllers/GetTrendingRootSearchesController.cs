using Microsoft.AspNetCore.Mvc;
using P2FK.IO.Models;
using P2FK.IO.Services;

namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class GetTrendingRootSearchesController : ControllerBase
    {
        public sealed class RecordTrendingSearchRequest
        {
            public string SearchString { get; set; } = "";
            public int ResultCount { get; set; } = 1;
        }

        private readonly RootSearchTrendService _trendService;

        public GetTrendingRootSearchesController(RootSearchTrendService trendService)
        {
            _trendService = trendService;
        }

        /// <summary>Returns the top successful searches from the last 24 hours.</summary>
        /// <remarks>
        /// Tracks successful free-text root searches plus explicit <c>#keyword</c> and
        /// <c>@profile</c> searches.
        /// Entries expire after 24 hours without another successful search.
        /// Ranking blends recency, repeat successful use, and result volume while
        /// damping spammy repeat searches with logarithmic weighting.
        /// </remarks>
        /// <param name="qty">Number of trending entries to return, 1–100 (default 100).</param>
        [HttpGet]
        [ProducesResponseType(typeof(List<TrendingRootSearchEntry>), StatusCodes.Status200OK)]
        public ActionResult Get(int qty = 100)
        {
            var results = _trendService.GetTrendingSearches(qty);
            return new JsonResult(results);
        }

        /// <summary>Records a successful explicit <c>#keyword</c> or <c>@profile</c> search.</summary>
        /// <param name="request">Search text with its prefix plus the count of results returned.</param>
        [HttpPost("record")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult Record([FromBody] RecordTrendingSearchRequest? request)
        {
            string searchString = request?.SearchString?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(searchString) ||
                (!searchString.StartsWith('#') && !searchString.StartsWith('@')))
            {
                return BadRequest(new { error = "searchString must start with # or @" });
            }

            int resultCount = Math.Clamp(request?.ResultCount ?? 0, 0, 5000);
            if (resultCount <= 0)
                return BadRequest(new { error = "resultCount must be greater than 0" });

            _trendService.RecordSuccessfulSearch(searchString, resultCount);
            return new JsonResult(new { recorded = true });
        }
    }
}
