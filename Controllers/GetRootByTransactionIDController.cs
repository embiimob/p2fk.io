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
        /// <summary>Retrieve a single on-chain root record (message / file inscription) by its transaction ID.</summary>
        /// <remarks>
        /// A "root" is any P2FK-protocol transaction that carries a message, file attachment, or other payload.
        /// Pass <c>verbose=true</c> to include the full output address list and file metadata.
        /// For altchains pass <c>blockchain=LTC|DOG|MZC</c> — the <c>mainnet</c> flag is ignored for non-BTC chains.
        /// </remarks>
        /// <param name="id">64-character hexadecimal transaction ID.</param>
        /// <param name="mainnet">true = Bitcoin mainnet; false = Bitcoin testnet (default true).</param>
        /// <param name="verbose">true = include extended payload metadata in the response.</param>
        /// <param name="blockchain">Target blockchain: BTC (default), LTC, DOG, or MZC.</param>
        [HttpGet("{id}")]
        public async Task<ActionResult> Get(string id, bool mainnet = true, bool verbose = false, string blockchain = "BTC")
        {
            // Regular expression for cryptocurrency transaction ID validation
            string pattern = @"^[0-9a-fA-F]{64}$";
            
            if (Regex.IsMatch(id, pattern))
            {
                string result = "";
                string arguments = "";

                if (blockchain != "BTC" && blockchain != "LTC" && blockchain != "DOG" && blockchain != "MZC")
                {
                    return Content("[\"invalid blockchain parameter, valid values are BTC, LTC, DOG, MZC\"]", "application/json");
                }

                if (blockchain == "LTC")
                {
                    arguments = "--versionbyte " + _wrapper.LTCVersionByte + " --getrootbytransactionid --password " + _wrapper.LTCRPCPassword + " --url " + _wrapper.LTCRPCURL + " --username " + _wrapper.LTCRPCUser + " --tid " + id;
                    if (verbose) { arguments = arguments + " --verbose"; }
                    result = await _wrapper.RunCommandAsync(_wrapper.LTCCLIPath, arguments, CancellationToken.None);
                }
                else if (blockchain == "DOG")
                {
                    arguments = "--versionbyte " + _wrapper.DOGVersionByte + " --getrootbytransactionid --password " + _wrapper.DOGRPCPassword + " --url " + _wrapper.DOGRPCURL + " --username " + _wrapper.DOGRPCUser + " --tid " + id;
                    if (verbose) { arguments = arguments + " --verbose"; }
                    result = await _wrapper.RunCommandAsync(_wrapper.DOGCLIPath, arguments, CancellationToken.None);
                }
                else if (blockchain == "MZC")
                {
                    arguments = "--versionbyte " + _wrapper.MZCVersionByte + " --getrootbytransactionid --password " + _wrapper.MZCRPCPassword + " --url " + _wrapper.MZCRPCURL + " --username " + _wrapper.MZCRPCUser + " --tid " + id;
                    if (verbose) { arguments = arguments + " --verbose"; }
                    result = await _wrapper.RunCommandAsync(_wrapper.MZCCLIPath, arguments, CancellationToken.None);
                }
                else if (mainnet)
                {
                    arguments = "--versionbyte " + _wrapper.ProdVersionByte + " --getrootbytransactionid --password " + _wrapper.ProdRPCPassword + " --url " + _wrapper.ProdRPCURL + " --username " + _wrapper.ProdRPCUser +" --tid " + id;
                    if (verbose) { arguments = arguments + " --verbose"; }
                    result = await _wrapper.RunCommandAsync(_wrapper.ProdCLIPath, arguments, CancellationToken.None);
                }
                else
                {
                    arguments = "--versionbyte " + _wrapper.TestVersionByte + " --getrootbytransactionid --password " + _wrapper.TestRPCPassword + " --url " + _wrapper.TestRPCURL + " --username " + _wrapper.TestRPCUser + " --tid " + id;
                    if (verbose) { arguments = arguments + " --verbose"; }
                    result = await _wrapper.RunCommandAsync(_wrapper.TestCLIPath, arguments, CancellationToken.None);
                }

                return Content(result, "application/json");
            }
            else { return Content("[\"invalid transaction id format\"]", "application/json"); }
        }


    }
}
