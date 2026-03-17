using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using static Microsoft.Extensions.Logging.EventSource.LoggingEventSource;


namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class GetRootByTransactionIDController : ControllerBase
    {
        private readonly Wrapper _wrapper;

        public GetRootByTransactionIDController(Wrapper wrapper)
        {
            _wrapper = wrapper;
        }

        // GET <GetRootByTransactionIDController>/5
        [HttpGet("{id}")]
        public async Task<ActionResult> Get(string id, bool mainnet = true, bool verbose = false)
        {
            // Regular expression for cryptocurrency transaction ID validation
            string pattern = @"^[0-9a-fA-F]{64}$";
            
            if (Regex.IsMatch(id, pattern))
            {
                string result = "";
                string arguments = "";

                if (mainnet)
                {
                    arguments = "--versionbyte " + _wrapper.ProdVersionByte + " --getrootbytransactionid --password " + _wrapper.ProdRPCPassword + " --url " + _wrapper.ProdRPCURL + " --username " + _wrapper.ProdRPCUser +" --tid " + id;
                    if (verbose) { arguments = arguments + " --verbose"; }
                    result = await _wrapper.RunCommandAsync(_wrapper.ProdCLIPath, arguments, HttpContext.RequestAborted);
                }
                else
                {
                    arguments = "--versionbyte " + _wrapper.TestVersionByte + " --getrootbytransactionid --password " + _wrapper.TestRPCPassword + " --url " + _wrapper.TestRPCURL + " --username " + _wrapper.TestRPCUser + " --tid " + id;
                    if (verbose) { arguments = arguments + " --verbose"; }
                    result = await _wrapper.RunCommandAsync(_wrapper.TestCLIPath, arguments, HttpContext.RequestAborted);
                }

                return Content(result, "application/json");
            }
            else { return Content("[\"invalid transaction id format\"]", "application/json"); }
        }


    }
}
