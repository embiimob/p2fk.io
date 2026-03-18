using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Text.RegularExpressions;

namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class GetSidechainMessageController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<GetSidechainMessageController> _logger;

        public GetSidechainMessageController(IHttpClientFactory httpClientFactory, ILogger<GetSidechainMessageController> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        // GET <GetSidechainMessageController>/{txid}
        // Proxies https://bitfossil.org/{txid}/MSG1 to avoid browser CORS restrictions.
        [HttpGet("{txid}")]
        public async Task<ActionResult> Get(string txid)
        {
            if (!Regex.IsMatch(txid, @"^[0-9a-fA-F]{64}$"))
                return BadRequest("invalid transaction id format");

            try
            {
                var client = _httpClientFactory.CreateClient("bitfossil");
                var resp = await client.GetAsync($"{txid}/MSG1", HttpContext.RequestAborted);
                if (!resp.IsSuccessStatusCode)
                    return StatusCode((int)resp.StatusCode);

                var text = await resp.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                return Content(text, "text/plain");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error proxying bitfossil.org message for txid {TxId}", txid);
                return StatusCode((int)HttpStatusCode.BadGateway, "upstream request failed");
            }
        }
    }
}
