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
        public ActionResult Get(string address, bool mainnet = true, bool verbose = false)
        {
            // Regular expression for cryptocurrency address validation
            string pattern = @"^[a-zA-Z0-9][a-km-zA-HJ-NP-Z1-9]{25,34}$";
            if (Regex.IsMatch(address, pattern))
            {
                Wrapper wrapper = new Wrapper();
                string result = "";
                string arguments = "";

                if (mainnet)
                {
                    arguments = "--versionbyte " + wrapper.ProdVersionByte + " --getrootsbyaddress --password " + wrapper.ProdRPCPassword + " --url " + wrapper.ProdRPCURL + " --username " + wrapper.ProdRPCUser + " --address " + address;
                    if (verbose) { arguments = arguments + " --verbose"; }
                    result = wrapper.RunCommand(wrapper.ProdCLIPath, arguments);
                }
                else
                {
                    arguments = "--versionbyte " + wrapper.TestVersionByte + " --getrootsbyaddress --password " + wrapper.TestRPCPassword + " --url " + wrapper.TestRPCURL + " --username " + wrapper.TestRPCUser + " --address " + address;
                    if (verbose) { arguments = arguments + " --verbose"; }
                    result = wrapper.RunCommand(wrapper.TestCLIPath, arguments);
                }

                return Content(result, "application/json");
            }
            else { return Content("[\"invalid address format\"]", "application/json"); }
        }


    }
}
