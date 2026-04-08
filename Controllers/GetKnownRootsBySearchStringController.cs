using Microsoft.AspNetCore.Mvc;
using P2FK.IO.Services;
using System.Runtime.Versioning;

namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    [SupportedOSPlatform("windows")]
    public class GetKnownRootsBySearchStringController : ControllerBase
    {
        private readonly WindowsSearchService _searchService;

        public GetKnownRootsBySearchStringController(WindowsSearchService searchService)
        {
            _searchService = searchService;
        }

        /// <summary>Full-text search for on-chain messages (roots) across the indexed blockchain.</summary>
        /// <remarks>
        /// Uses Windows Search to query the local index of on-chain message data (P2FK roots).
        /// Supports wildcard searches — e.g. pass <c>searchString=*</c> to retrieve the most recent messages.
        /// Results are sorted by relevance / date and can be paginated via <c>skip</c> + <c>qty</c>.
        /// Chain selection: pass <c>mainnet=false</c> for Bitcoin testnet; pass <c>blockchain=LTC|DOG|MZC</c> for sidechains.
        /// </remarks>
        /// <param name="searchString">Search query (max 2048 characters). Use <c>*</c> to return all results.</param>
        /// <param name="qty">Number of results to return, 1–5000 (default 10).</param>
        /// <param name="skip">Number of results to skip for pagination (default 0).</param>
        /// <param name="mainnet">true = Bitcoin mainnet; false = Bitcoin testnet. Ignored when blockchain ≠ BTC.</param>
        /// <param name="blockchain">Target blockchain: BTC (default), LTC, DOG, or MZC.</param>
        /// <param name="showSystemFiles">When false, system-type messages (SEC/OBJ/LST/BRN/PRO/BUY/GIV) and empty messages are excluded before caching and returning results (default true).</param>
        [HttpGet]
        public async Task<ActionResult> Get(string searchString = "", int qty = 10, int skip = 0, bool mainnet = true, string blockchain = "BTC", bool showSystemFiles = true)
        {
            if (searchString.Length > 2048)
                return BadRequest("[\"invalid search string\"]");

            if (blockchain != "BTC" && blockchain != "LTC" && blockchain != "DOG" && blockchain != "MZC")
                return Content("[\"invalid blockchain parameter, valid values are BTC, LTC, DOG, MZC\"]", "application/json");

            qty = Math.Clamp(qty, 1, 5000);
            skip = Math.Clamp(skip, 0, 4999);
            qty = Math.Min(qty, 5000 - skip);

            // Translate public API params to the internal chain identifier used by the service.
            // mainnet=false with blockchain=BTC means Bitcoin testnet; all other combinations
            // map directly to the blockchain value.
            string effectiveChain = (blockchain == "BTC" && !mainnet) ? "BTC-testnet" : blockchain;

            var results = await _searchService.SearchRootsAsync(searchString, qty, skip, effectiveChain, showSystemFiles);
            return new JsonResult(results);
        }
    }
}
