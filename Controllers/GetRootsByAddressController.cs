using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class GetRootsByAddressController : ControllerBase
    {
        private readonly Wrapper _wrapper;

        public GetRootsByAddressController(Wrapper wrapper)
        {
            _wrapper = wrapper;
        }

        // GET <GetRootsByAddressController>/5
        /// <summary>Get all root records (messages / file inscriptions) associated with a blockchain address.</summary>
        /// <remarks>Returns every P2FK root transaction sent from or to this address. Verbose mode is disabled server-side to protect performance.</remarks>
        /// <param name="address">Cryptocurrency address (26–34 base58 characters).</param>
        /// <param name="mainnet">true = Bitcoin mainnet; false = Bitcoin testnet (default true).</param>
        /// <param name="verbose">Reserved — always treated as false.</param>
        [HttpGet("{address}")]
        public async Task<ActionResult> Get(string address, bool mainnet = true, bool verbose = false)
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
                    arguments = "--versionbyte " + _wrapper.ProdVersionByte + " --getrootsbyaddress --password " + _wrapper.ProdRPCPassword + " --url " + _wrapper.ProdRPCURL + " --username " + _wrapper.ProdRPCUser + " --address " + address;
                    if (verbose) { arguments = arguments + " --verbose"; }
                    result = await _wrapper.RunCommandAsync(_wrapper.ProdCLIPath, arguments, HttpContext.RequestAborted);
                }
                else
                {
                    arguments = "--versionbyte " + _wrapper.TestVersionByte + " --getrootsbyaddress --password " + _wrapper.TestRPCPassword + " --url " + _wrapper.TestRPCURL + " --username " + _wrapper.TestRPCUser + " --address " + address;
                    if (verbose) { arguments = arguments + " --verbose"; }
                    result = await _wrapper.RunCommandAsync(_wrapper.TestCLIPath, arguments, HttpContext.RequestAborted);
                }

                return Content(result, "application/json");
            }
            else { return Content("[\"invalid address format\"]", "application/json"); }
        }


    }
}
