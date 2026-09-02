using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class GetPublicAddressByKeywordController : ControllerBase
    {
        private readonly Wrapper _wrapper;
        private readonly Services.RootSearchTrendService _trendService;

        public GetPublicAddressByKeywordController(Wrapper wrapper, Services.RootSearchTrendService trendService)
        {
            _wrapper = wrapper;
            _trendService = trendService;
        }

        // GET <GetPublicAddressByKeywordController>/5
        /// <summary>Resolve a keyword/hashtag to its registered blockchain address.</summary>
        /// <remarks>Keywords are on-chain aliases. This is the reverse lookup of <c>GetKeywordByPublicAddress</c>.</remarks>
        /// <param name="keyword">Keyword or hashtag (without the # prefix).</param>
        /// <param name="mainnet">true = Bitcoin mainnet; false = Bitcoin testnet (default true).</param>
        [HttpGet("{keyword}")]
        public async Task<ActionResult> Get(string keyword, bool mainnet = true)
        {
           
                string arguments = "";
                string result = "";

                if (mainnet)
                {
                    arguments = "--versionbyte " + _wrapper.ProdVersionByte + " --getpublicaddressbykeyword --keyword " + keyword;
                    result = await _wrapper.RunCommandAsync(_wrapper.ProdCLIPath, arguments, HttpContext.RequestAborted);
                }
                else { arguments = "--versionbyte " + _wrapper.TestVersionByte + " --getpublicaddressbykeyword --keyword " + keyword;
                    result = await _wrapper.RunCommandAsync(_wrapper.TestCLIPath, arguments, HttpContext.RequestAborted);
                }

                if (Request.Headers.TryGetValue("X-Track-Trending-Search", out var trackedSearchValues))
                {
                    string trackedSearch = trackedSearchValues.ToString().Trim();
                    string normalizedResult = result.Trim().Trim('"');
                    if (trackedSearch == "#" + keyword &&
                        Regex.IsMatch(normalizedResult, @"^[a-zA-Z0-9][a-km-zA-HJ-NP-Z1-9]{25,34}$"))
                    {
                        int resultCount = 1;
                        if (Request.Headers.TryGetValue("X-Trending-Result-Count", out var resultCountValues))
                            int.TryParse(resultCountValues.ToString(), out resultCount);
                        _trendService.RecordSuccessfulSearch(trackedSearch, Math.Max(1, resultCount));
                    }
                }

                return Content(result, "application/json");
            

        }


    }
}
