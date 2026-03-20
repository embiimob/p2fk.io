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

        // GET <GetKnownObjectsBySearchStringController>/{searchString}?qty=10&skip=0&blockchain=
        [HttpGet("{searchString}")]
        public async Task<ActionResult> Get(
            string searchString,
            int qty = 10,
            int skip = 0,
            string blockchain = "")
        {
            if (string.IsNullOrWhiteSpace(searchString))
                return Content("[\"search string is required\"]", "application/json");

            if (searchString.Length > 2048)
                searchString = searchString[..2048];

            if (!string.IsNullOrEmpty(blockchain) &&
                blockchain != "BTC" && blockchain != "LTC" &&
                blockchain != "DOG" && blockchain != "MZC")
            {
                return Content(
                    "[\"invalid blockchain parameter, valid values are BTC, LTC, DOG, MZC or empty for all\"]",
                    "application/json");
            }

            if (qty < 1) qty = 1;
            if (qty > 100) qty = 100;
            if (skip < 0) skip = 0;

            string result = await _searchService.SearchObjectsAsync(
                searchString, qty, skip, blockchain);
            return Content(result, "application/json");
        }
    }
}
