using Microsoft.AspNetCore.Mvc;
using P2FK.IO.Services;

namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class GetCacheStatusController : ControllerBase
    {
        private readonly CacheStatusService _cacheStatus;

        public GetCacheStatusController(CacheStatusService cacheStatus)
        {
            _cacheStatus = cacheStatus;
        }

        /// <summary>Returns the current warm-cache status and per-bucket entry counts.</summary>
        /// <remarks>
        /// Reports whether a warm cycle is in progress, when the last cycle ran, how long it
        /// took, when the next cycle is scheduled, and how many entries are stored under each
        /// internal cache key.
        ///
        /// Cache keys follow the pattern <c>{type}:{searchString}:{blockchain}:{flag}</c>
        /// where <c>type</c> is <c>roots</c>, <c>objects</c>, or <c>profiles</c>;
        /// <c>blockchain</c> is the chain identifier (e.g. <c>btc-testnet</c>, <c>ltc</c>)
        /// or empty for the combined all-chains bucket; and <c>flag</c> is the
        /// <c>showSystemFiles</c> boolean.
        ///
        /// This endpoint has negligible overhead and is safe to poll frequently.
        /// </remarks>
        [HttpGet]
        public ActionResult Get()
        {
            var counts = _cacheStatus.EntryCounts;
            int totalEntries = counts.Values.Sum();

            return new JsonResult(new
            {
                isWarming              = _cacheStatus.IsWarming,
                lastWarmStarted        = _cacheStatus.LastWarmStarted,
                lastWarmCompleted      = _cacheStatus.LastWarmCompleted,
                lastWarmDurationMs     = _cacheStatus.LastWarmDurationMs,
                nextWarmAt             = _cacheStatus.NextWarmAt,
                currentRefreshIntervalMs = _cacheStatus.CurrentRefreshIntervalMs,
                totalEntries,
                entryCounts            = counts,
            });
        }
    }
}
