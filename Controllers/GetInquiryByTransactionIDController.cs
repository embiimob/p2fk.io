using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class GetInquiryByTransactionIDController : ControllerBase
    {
        private readonly Wrapper _wrapper;

        public GetInquiryByTransactionIDController(Wrapper wrapper)
        {
            _wrapper = wrapper;
        }

        // GET <GetInquiryByTransactionIDController>/5
        [HttpGet("{id}")]
        public async Task<ActionResult> Get(string id, bool mainnet = true, bool verbose = false)
        {
            // Regular expression for cryptocurrency address validation
            string pattern = @"^[0-9a-fA-F]{64}$";
            if (Regex.IsMatch(id, pattern))
            {
                string arguments = "";
                string result = "";

                if (mainnet)
                {
                    arguments = "--versionbyte " + _wrapper.ProdVersionByte + " --getinquirybytransactionid --password " + _wrapper.ProdRPCPassword + " --url " + _wrapper.ProdRPCURL + " --username " + _wrapper.ProdRPCUser + " --tid " + id;
                    if (verbose) { arguments = arguments + " --verbose"; }
                    result = await _wrapper.RunCommandAsync(_wrapper.ProdCLIPath, arguments, HttpContext.RequestAborted);
                }
                else { arguments = "--versionbyte " + _wrapper.TestVersionByte + " --getinquirybytransactionid --password " + _wrapper.TestRPCPassword + " --url " + _wrapper.TestRPCURL + " --username " + _wrapper.TestRPCUser + " --tid " + id;
                    if (verbose) { arguments = arguments + " --verbose"; }
                    result = await _wrapper.RunCommandAsync(_wrapper.TestCLIPath, arguments, HttpContext.RequestAborted);
                }
              

                return Content(result, "application/json");
            }
            else { return Content("[\"invalid transaction id format\"]", "application/json"); }

        }


    }
}
