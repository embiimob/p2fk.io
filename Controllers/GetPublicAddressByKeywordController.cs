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

        public GetPublicAddressByKeywordController(Wrapper wrapper)
        {
            _wrapper = wrapper;
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


                return Content(result, "application/json");
            

        }


    }
}
