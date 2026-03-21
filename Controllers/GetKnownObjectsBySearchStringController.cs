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
        public async Task<ActionResult> Get(string searchString = "", int qty = 10, int skip = 0)
        {
            if (searchString.Length > 2048)
                return BadRequest("[\"invalid search string\"]");

            qty = Math.Clamp(qty, 1, 100);
            skip = Math.Max(skip, 0);

            var results = await _searchService.SearchObjectsAsync(searchString, qty, skip);
            return new JsonResult(results);
        }
    }
}
