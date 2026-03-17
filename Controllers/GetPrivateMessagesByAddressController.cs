using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class GetPublicMessagesByAddressController : ControllerBase
    {
        private readonly Wrapper _wrapper;

        public GetPublicMessagesByAddressController(Wrapper wrapper)
        {
            _wrapper = wrapper;
        }

        // GET <GetPublicMessagesByAddressController>/5
        [HttpGet("{address}")]
        public async Task<ActionResult> Get(string address, int skip=0, int qty = 10, bool mainnet = true)
        {
            // Regular expression for cryptocurrency address validation
            string pattern = @"^[a-zA-Z0-9][a-km-zA-HJ-NP-Z1-9]{25,34}$";
            if (Regex.IsMatch(address, pattern))
            {
                string arguments = "";
                string result = "";

                if (mainnet)
                {
                    arguments = "--versionbyte " + _wrapper.ProdVersionByte + " --getpublicmessagesbyaddress --password " + _wrapper.ProdRPCPassword + " --url " + _wrapper.ProdRPCURL + " --username " + _wrapper.ProdRPCUser + " --skip " + skip + " --qty " + qty + " --address " + address;
                    result = await _wrapper.RunCommandAsync(_wrapper.ProdCLIPath, arguments, HttpContext.RequestAborted);
                }
                else { arguments = "--versionbyte " + _wrapper.TestVersionByte + " --getpublicmessagesbyaddress --password " + _wrapper.TestRPCPassword + " --url " + _wrapper.TestRPCURL + " --username " + _wrapper.TestRPCUser + " --skip " + skip + " --qty " + qty + " --address " + address;
                    result = await _wrapper.RunCommandAsync(_wrapper.TestCLIPath, arguments, HttpContext.RequestAborted);
                }
                           
                


                return Content(result, "application/json");
            }
            else { return Content("[\"invalid address format\"]", "application/json"); }

        }


    }
}
