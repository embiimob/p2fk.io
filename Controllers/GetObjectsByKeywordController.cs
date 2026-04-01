using Microsoft.AspNetCore.Mvc;
using System.Net;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class GetObjectsByKeywordController : ControllerBase
    {
        private readonly Wrapper _wrapper;

        public GetObjectsByKeywordController(Wrapper wrapper)
        {
            _wrapper = wrapper;
        }

        // GET <GetObjectsByKeywordController>/5
        /// <summary>Get all P2FK digital objects tagged with a specific keyword/hashtag.</summary>
        /// <remarks>Returns every object whose metadata includes this keyword. Useful for browsing collections or thematic searches.</remarks>
        /// <param name="keyword">Keyword or hashtag (without the # prefix).</param>
        /// <param name="skip">Number of results to skip (default 0).</param>
        /// <param name="qty">Number of results to return; -1 = all (default -1).</param>
        /// <param name="mainnet">true = Bitcoin mainnet; false = Bitcoin testnet (default true).</param>
        [HttpGet("{keyword}")]
        public async Task<ActionResult> Get(string keyword, int skip = 0, int qty = -1, bool mainnet = true)
        {

      
            string result = "";
            string arguments = "";

            if (mainnet)
            {
                arguments = "--versionbyte " + _wrapper.ProdVersionByte + " --getobjectsbykeyword --password " + _wrapper.ProdRPCPassword + " --url " + _wrapper.ProdRPCURL + " --username " + _wrapper.ProdRPCUser + " --skip " + skip + " --qty " + qty + " --keyword " + keyword;
                result = await _wrapper.RunCommandAsync(_wrapper.ProdCLIPath, arguments, HttpContext.RequestAborted);
            }
            else
            {
                arguments = "--versionbyte " + _wrapper.TestVersionByte + " --getobjectsbykeyword --password " + _wrapper.TestRPCPassword + " --url " + _wrapper.TestRPCURL + " --username " + _wrapper.TestRPCUser + " --skip " + skip + " --qty " + qty + " --keyword " + keyword;
                result = await _wrapper.RunCommandAsync(_wrapper.TestCLIPath, arguments, HttpContext.RequestAborted);
            }

            return Content(result, "application/json");
        }

       
    }
}
