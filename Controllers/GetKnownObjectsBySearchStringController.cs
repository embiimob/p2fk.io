using Microsoft.AspNetCore.Mvc;
using P2FK.IO.Services;
using System.Runtime.Versioning;

namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    [SupportedOSPlatform("windows")]
    public class GetKnownObjectsBySearchStringController : ControllerBase
    {
        private readonly WindowsSearchService _searchService;

        public GetKnownObjectsBySearchStringController(WindowsSearchService searchService)
        {
            _searchService = searchService;
        }

        [HttpGet]
        public async Task<ActionResult> Get(string searchString = "", int qty = 10, int skip = 0, bool mainnet = true, string blockchain = "BTC")
        {
            if (searchString.Length > 2048)
                return BadRequest("[\"invalid search string\"]");

            if (blockchain != "BTC" && blockchain != "LTC" && blockchain != "DOG" && blockchain != "MZC")
                return Content("[\"invalid blockchain parameter, valid values are BTC, LTC, DOG, MZC\"]", "application/json");

            qty = Math.Clamp(qty, 1, 1000);
            skip = Math.Clamp(skip, 0, 999);
            qty = Math.Min(qty, 1000 - skip);

            // Translate public API params to the internal chain identifier used by the service.
            // mainnet=false with blockchain=BTC means Bitcoin testnet; all other combinations
            // map directly to the blockchain value.
            string effectiveChain = (blockchain == "BTC" && !mainnet) ? "BTC-testnet" : blockchain;

            var results = await _searchService.SearchObjectsAsync(searchString, qty, skip, effectiveChain);
            return new JsonResult(results);
        }
    }
}
