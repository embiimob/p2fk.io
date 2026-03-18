using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

namespace P2FK.IO.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class GetSidechainMessageController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public GetSidechainMessageController(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        // GET <GetSidechainMessageController>/{txid}
        // Fetches the MSG1 text for a sidechain (LTC/DOG/MZC) transaction from bitfossil.org.
        // This server-side proxy avoids browser CORS restrictions on direct bitfossil.org fetches.
        [HttpGet("{txid}")]
        public async Task<ActionResult> Get(string txid)
        {
            if (!Regex.IsMatch(txid, @"^[0-9a-fA-F]{64}$"))
                return BadRequest("Invalid transaction id format");

            var client = _httpClientFactory.CreateClient("bitfossil");
            try
            {
                var response = await client.GetAsync(
                    $"{txid}/MSG1",
                    HttpContext.RequestAborted);

                if (!response.IsSuccessStatusCode)
                    return NotFound("Message not found");

                var text = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                return Content(text, "text/plain; charset=utf-8");
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, "Request cancelled");
            }
            catch (Exception ex)
            {
                return StatusCode(502, $"Failed to fetch message: {ex.Message}");
            }
        }
    }
}
