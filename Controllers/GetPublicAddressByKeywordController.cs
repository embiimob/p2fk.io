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
