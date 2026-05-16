using Microsoft.AspNetCore.Mvc;
using P2FK.IO.Models;
using P2FK.IO.Services;

namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class GetTrendingRootSearchesController : ControllerBase
    {
        private readonly RootSearchTrendService _trendService;

        public GetTrendingRootSearchesController(RootSearchTrendService trendService)
        {
            _trendService = trendService;
        }

        /// <summary>Returns the top successful free-text root searches from the last 24 hours.</summary>
        /// <remarks>
        /// Only successful non-empty, non-wildcard root searches are tracked.
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
    }
}
