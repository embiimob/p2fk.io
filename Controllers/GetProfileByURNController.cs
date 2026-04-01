using Microsoft.AspNetCore.Mvc;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class GetProfileByURNController : ControllerBase
    {
        private readonly Wrapper _wrapper;

        public GetProfileByURNController(Wrapper wrapper)
        {
            _wrapper = wrapper;
        }

        // GET <GetProfileByURNController>/5
        /// <summary>Look up a user profile by its URN (display name / handle).</summary>
        /// <remarks>The URN is the human-readable identity registered on-chain, e.g. <c>embii4u</c>. Returns the full profile record including bio, image URN, links, and creation dates.</remarks>
        /// <param name="urn">Profile URN / handle — URL-encode slashes as <c>%2F</c>.</param>
        /// <param name="mainnet">true = Bitcoin mainnet; false = Bitcoin testnet (default true).</param>
        [HttpGet("{urn}")]
        public async Task<ActionResult> Get(string urn, bool mainnet = true)
        {
            // Regular expression for cryptocurrency address validation
           
                string arguments = "";
                string result = "";

                if (mainnet)
                {
                    arguments = "--versionbyte " + _wrapper.ProdVersionByte + " --getprofilebyurn --password " + _wrapper.ProdRPCPassword + " --url " + _wrapper.ProdRPCURL + " --username " + _wrapper.ProdRPCUser + " --urn \"" + urn.Replace("%2F", "/") + "\"";
                result = await _wrapper.RunCommandAsync(_wrapper.ProdCLIPath, arguments, HttpContext.RequestAborted);
                }
                else { arguments = "--versionbyte " + _wrapper.TestVersionByte + " --getprofilebyurn --password " + _wrapper.TestRPCPassword + " --url " + _wrapper.TestRPCURL + " --username " + _wrapper.TestRPCUser + " --urn \"" + urn.Replace("%2F", "/") + "\"";
                result = await _wrapper.RunCommandAsync(_wrapper.TestCLIPath, arguments, HttpContext.RequestAborted);
                }

                return Content(result, "application/json");
           

        }


    }
}
