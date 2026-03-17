using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class GetKeywordByPublicAddressController : ControllerBase
    {
        private readonly Wrapper _wrapper;

        public GetKeywordByPublicAddressController(Wrapper wrapper)
        {
            _wrapper = wrapper;
        }

        // GET <GetKeywordByPublicAddressController >/5
        [HttpGet("{address}")]
        public async Task<ActionResult> Get(string address)
        {
            // Regular expression for cryptocurrency address validation
            string pattern = @"^[a-zA-Z0-9][a-km-zA-HJ-NP-Z1-9]{25,34}$";
            if (Regex.IsMatch(address, pattern))
            {
                string arguments = "";
                string result = "";
                               
                    arguments = "--getkeywordbypublicaddress --address " + address;
                    result = await _wrapper.RunCommandAsync(_wrapper.ProdCLIPath, arguments, HttpContext.RequestAborted);
              

                return Content(result, "application/json");
            }
            else { return Content("[\"invalid address format\"]", "application/json"); }

        }


    }
}
