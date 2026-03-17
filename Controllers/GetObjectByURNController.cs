using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class GetObjectByURNController : ControllerBase
    {
        private readonly Wrapper _wrapper;

        public GetObjectByURNController(Wrapper wrapper)
        {
            _wrapper = wrapper;
        }

        // GET <GetObjectByURNController>/5
        [HttpGet("{urn}")]
        public async Task<ActionResult> Get(string urn, bool mainnet = true)
        {
            
                string arguments = "";
                string result = "";

                if (mainnet)
                {
                    arguments = "--versionbyte " + _wrapper.ProdVersionByte + " --getobjectbyurn --password " + _wrapper.ProdRPCPassword + " --url " + _wrapper.ProdRPCURL + " --username " + _wrapper.ProdRPCUser + " --urn \"" + urn.Replace("%2F","/")+ "\"";
                    result = await _wrapper.RunCommandAsync(_wrapper.ProdCLIPath, arguments, HttpContext.RequestAborted);
                }
                else { arguments = "--versionbyte " + _wrapper.TestVersionByte + " --getobjectbyurn --password " + _wrapper.TestRPCPassword + " --url " + _wrapper.TestRPCURL + " --username " + _wrapper.TestRPCUser + " --urn \"" + urn.Replace("%2F", "/") + "\"";
                    result = await _wrapper.RunCommandAsync(_wrapper.TestCLIPath, arguments, HttpContext.RequestAborted);
                }


                return Content(result, "application/json");
            

        }


    }
}
