﻿using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace p2fk.io.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class GetRootsByAddressController : ControllerBase
    {
       
        // GET <GetRootsByAddressController>/5
        [HttpGet("{address}")]
        public ActionResult Get(string address)
        {
            // Regular expression for cryptocurrency address validation
            string pattern = @"^[a-zA-Z0-9][a-km-zA-HJ-NP-Z1-9]{25,34}$";
            if (Regex.IsMatch(address, pattern))
            {
                Wrapper wrapper = new Wrapper();
                string arguments = "--versionbyte " + wrapper.VersionByte + " --getrootsbyaddress --password " + wrapper.RPCPassword + " --url " + wrapper.RPCURL + " --username " + wrapper.RPCUser + " --address " + address;

                string result = "";

                result = wrapper.RunCommand(wrapper.CLIPath, arguments);

                return Content(result, "application/json");
            }
            else { return Content("[\"invalid address format\"]", "application/json"); }
        }


    }
}