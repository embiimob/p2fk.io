using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class GetObjectsByAddressController : ControllerBase
    {
        private readonly Wrapper _wrapper;

        public GetObjectsByAddressController(Wrapper wrapper)
        {
            _wrapper = wrapper;
        }

        // GET <GetObjectsByAddressController>/5
        /// <summary>Get all P2FK digital objects associated with a given address (created or owned).</summary>
        /// <remarks>Returns all objects linked to this address as creator or owner, with ownership units and listing prices. Use <c>skip</c> + <c>qty</c> for pagination; pass <c>qty=-1</c> to return everything.</remarks>
        /// <param name="address">Cryptocurrency address (26–34 base58 characters).</param>
        /// <param name="skip">Number of results to skip (default 0).</param>
        /// <param name="qty">Number of results to return; -1 = all (default -1).</param>
        /// <param name="mainnet">true = Bitcoin mainnet; false = Bitcoin testnet (default true).</param>
        /// <param name="verbose">Reserved — always treated as false.</param>
        [HttpGet("{address}")]
        public async Task<ActionResult> Get(string address, int skip = 0, int qty = -1, bool mainnet = true, bool verbose = false)
        {
            verbose = false; // Force verbose off - third party callers causing issues with verbose mode

      
                // Regular expression for cryptocurrency address validation
            string pattern = @"^[a-zA-Z0-9][a-km-zA-HJ-NP-Z1-9]{25,34}$";
            if (Regex.IsMatch(address, pattern))
            {
                string result = "";
                string arguments = "";

                if (mainnet)
                {
                    arguments = "--versionbyte " + _wrapper.ProdVersionByte + " --getobjectsbyaddress --password " + _wrapper.ProdRPCPassword + " --url " + _wrapper.ProdRPCURL + " --username " + _wrapper.ProdRPCUser + " --skip " + skip + " --qty " + qty + " --address " + address;
                    if (verbose) { arguments = arguments + " --verbose"; }
                    result = await _wrapper.RunCommandAsync(_wrapper.ProdCLIPath, arguments, HttpContext.RequestAborted);
                }
                else
                {
                    arguments = "--versionbyte " + _wrapper.TestVersionByte + " --getobjectsbyaddress --password " + _wrapper.TestRPCPassword + " --url " + _wrapper.TestRPCURL + " --username " + _wrapper.TestRPCUser + " --skip " + skip + " --qty " + qty + " --address " + address;
                    if (verbose) { arguments = arguments + " --verbose"; }
                    result = await _wrapper.RunCommandAsync(_wrapper.TestCLIPath, arguments, HttpContext.RequestAborted);
                }

                return Content(result, "application/json");
            }
            else { return Content("[\"invalid address format\"]", "application/json"); }
        }

       
    }
}
