using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace P2FK.IO.Controllers
{
    [Route("root")]
    [ApiController]
    public class RootViewerController : ControllerBase
    {
        private readonly Wrapper _wrapper;

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg" };
        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".wav", ".ogg", ".flac", ".aac", ".m4a" };
        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".webm", ".ogv", ".mov" };
        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".txt", ".xml", ".csv", ".md" };
        private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".pdf" };

        public RootViewerController(Wrapper wrapper)
        {
            _wrapper = wrapper;
        }

        [HttpGet("{txid}")]
        [HttpGet("{txid}/index.html")]
        [HttpGet("{txid}/index.htm")]
        public IActionResult Get(string txid)
        {
            if (!Regex.IsMatch(txid, @"^[0-9a-fA-F]{64}$"))
                return NotFound();

            var rootJsonPath = Path.Combine(_wrapper.RootPath, txid, "ROOT.json");
            if (!System.IO.File.Exists(rootJsonPath))
                return NotFound();

            string json;
            try
            {
                json = System.IO.File.ReadAllText(rootJsonPath, Encoding.UTF8);
            }
            catch
            {
                return NotFound();
            }

            JsonElement root;
            try
            {
                root = JsonSerializer.Deserialize<JsonElement>(json);
            }
            catch
            {
                return Content("<html><body>Error parsing ROOT.json</body></html>", "text/html");
            }

            var html = BuildHtml(txid, root);
            return Content(html, "text/html; charset=utf-8");
        }

        private string BuildHtml(string txid, JsonElement root)
        {
            // ── Extract fields ───────────────────────────────────────────────
            string blockDate   = GetString(root, "BlockDate");
            string blockHeight = GetString(root, "BlockHeight");
            string confs       = GetString(root, "Confirmations");
            string totalBytes  = GetString(root, "TotalByteSize");
            string hash        = GetString(root, "Hash");
            string signedBy    = GetString(root, "SignedBy");
            string signature   = GetString(root, "Signature");
            bool   signed      = root.TryGetProperty("Signed", out var sv) && sv.GetBoolean();
            string buildDate   = GetString(root, "BuildDate");
            bool   cached      = root.TryGetProperty("Cached", out var cv) && cv.GetBoolean();

            // Format block date nicely
            string blockDateDisplay = blockDate;
            if (DateTime.TryParse(blockDate, out var bd))
                blockDateDisplay = bd.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";

            // ── Files ────────────────────────────────────────────────────────
            var files = new List<(string name, long size)>();
            if (root.TryGetProperty("File", out var fileProp) && fileProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var f in fileProp.EnumerateObject())
                {
                    if (f.Name == "SIG" || f.Name == "LNK") continue;
                    long size = 0;
                    if (f.Value.ValueKind == JsonValueKind.Number) f.Value.TryGetInt64(out size);
                    files.Add((f.Name, size));
                }
            }

            // ── Messages ────────────────────────────────────────────────────
            var messages = new List<string>();
            if (root.TryGetProperty("Message", out var msgProp))
            {
                if (msgProp.ValueKind == JsonValueKind.Array)
                    foreach (var m in msgProp.EnumerateArray())
                        messages.Add(m.GetString() ?? "");
                else if (msgProp.ValueKind == JsonValueKind.String)
                    messages.Add(msgProp.GetString() ?? "");
            }

            // ── Outputs ─────────────────────────────────────────────────────
            var outputs = new List<(string address, string amount)>();
            if (root.TryGetProperty("Output", out var outProp) && outProp.ValueKind == JsonValueKind.Object)
                foreach (var o in outProp.EnumerateObject())
                    outputs.Add((o.Name, o.Value.GetString() ?? ""));

            // Detect blockchain from the first output address version byte prefix
            string chainAbbrev      = DetectChain(outputs.Count > 0 ? outputs[0].address : "");
            string chainDisplayName = ChainDisplayName(chainAbbrev);

            // ── Keywords ────────────────────────────────────────────────────
            var keywords = new List<string>();
            if (root.TryGetProperty("Keyword", out var kwProp) && kwProp.ValueKind == JsonValueKind.Object)
                foreach (var k in kwProp.EnumerateObject())
                    keywords.Add(k.Name);

            var sb = new StringBuilder();

            // ─────────────────────────────────────────────────────────────────
            // HTML head
            // ─────────────────────────────────────────────────────────────────
            string shortTxid = txid.Length >= 16 ? txid[..8] + "…" + txid[^8..] : txid;

            // Pick first image for OG preview
            string ogImage = "https://p2fk.io/bitfossil.png";
            foreach (var (name, _) in files)
            {
                var ext = Path.GetExtension(name);
                if (ImageExtensions.Contains(ext))
                {
                    ogImage = $"https://p2fk.io/root/{txid}/{Uri.EscapeDataString(name)}";
                    break;
                }
            }

            sb.Append($@"<!DOCTYPE html>
<html lang=""en"">
<head>
  <meta charset=""UTF-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
  <title>Root Index {H(shortTxid)} – bitFossil</title>
  <link rel=""apple-touch-icon"" sizes=""180x180"" href=""/apple-touch-icon.png"">
  <link rel=""icon"" type=""image/png"" sizes=""32x32"" href=""/favicon-32x32.png"">
  <link rel=""icon"" type=""image/png"" sizes=""16x16"" href=""/favicon-16x16.png"">
  <meta property=""og:title"" content=""Root Index {H(shortTxid)} – bitFossil"">
  <meta property=""og:description"" content=""On-chain root record inscribed {H(blockDateDisplay)}"">
  <meta property=""og:image"" content=""{H(ogImage)}"">
  <meta property=""og:url"" content=""https://p2fk.io/root/{H(txid)}"">
  <meta property=""og:type"" content=""article"">
  <meta property=""og:site_name"" content=""bitFossil"">
  <meta name=""twitter:card"" content=""summary_large_image"">
  <meta name=""twitter:title"" content=""Root Index {H(shortTxid)} – bitFossil"">
  <meta name=""twitter:image"" content=""{H(ogImage)}"">
  <meta name=""twitter:site"" content=""@bitFossil"">
  <style>
    *, *::before, *::after {{ box-sizing: border-box; }}
    html {{ scroll-behavior: smooth; }}
    body {{
      background: #121212;
      color: #e0e0e0;
      font-family: Arial, sans-serif;
      margin: 0; padding: 0;
      min-height: 100vh;
    }}
    a {{ color: #03dac6; text-decoration: none; }}
    a:hover {{ color: #bb86fc; }}
    /* ── Navbar ── */
    #navbar {{
      position: fixed; top: 0; left: 0; right: 0; z-index: 2000;
      background: #1e1e1e;
      border-bottom: 2px solid #bb86fc;
      display: flex; align-items: center; gap: 10px;
      padding: 7px 14px; height: 54px;
    }}
    #navbar .brand {{
      display: flex; align-items: center; gap: 8px; flex-shrink: 0;
      text-decoration: none; color: #bb86fc;
      font-size: 1.15em; font-weight: bold;
    }}
    #navbar .brand img {{ width: 36px; height: 36px; border-radius: 4px; object-fit: cover; }}
    .nav-links {{
      display: flex; align-items: center; gap: 12px;
      flex-shrink: 0; margin-left: auto;
    }}
    .nav-links a {{
      color: #b0b0b0; font-size: 0.82em; white-space: nowrap;
      padding: 4px 8px; border-radius: 4px; border: 1px solid #333;
      transition: all 0.2s;
    }}
    .nav-links a:hover {{ background: #2a2a2a; color: #bb86fc; border-color: #bb86fc; }}
    /* ── Content ── */
    .page {{ max-width: 960px; margin: 0 auto; padding: 74px 16px 40px; }}
    .txid-header {{ word-break: break-all; color: #bb86fc; font-size: 1.05em; font-weight: bold; margin-bottom: 6px; }}
    .section {{ background: #1e1e1e; border-radius: 8px; padding: 16px 20px; margin-bottom: 18px; }}
    .section-title {{
      font-size: 0.78em; font-weight: bold; text-transform: uppercase;
      letter-spacing: 0.08em; color: #bb86fc; margin-bottom: 12px;
    }}
    .meta-grid {{ display: grid; grid-template-columns: max-content 1fr; gap: 4px 14px; font-size: 0.88em; }}
    .meta-label {{ color: #888; white-space: nowrap; }}
    .meta-value {{ word-break: break-all; }}
    .badge {{
      display: inline-block; padding: 2px 8px; border-radius: 4px;
      font-size: 0.75em; font-weight: bold; margin-left: 8px;
    }}
    .badge-signed {{ background: #1b5e20; color: #a5d6a7; }}
    .badge-cached {{ background: #1a237e; color: #90caf9; }}
    /* ── File list ── */
    .file-list {{ list-style: none; padding: 0; margin: 0; }}
    .file-list li {{ padding: 6px 0; border-bottom: 1px solid #2a2a2a; display: flex; align-items: center; gap: 10px; }}
    .file-list li:last-child {{ border-bottom: none; }}
    .file-icon {{ font-size: 1.2em; flex-shrink: 0; }}
    .file-name {{ flex: 1; word-break: break-all; }}
    .file-size {{ color: #888; font-size: 0.8em; flex-shrink: 0; white-space: nowrap; }}
    /* ── Media ── */
    .media-grid {{
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
      gap: 12px; margin-bottom: 8px;
    }}
    .media-card {{
      background: #121212; border-radius: 6px; overflow: hidden;
      border: 1px solid #2a2a2a;
    }}
    .media-card img {{
      width: 100%; display: block; max-height: 300px;
      object-fit: contain; background: #000;
    }}
    .media-card audio, .media-card video {{ width: 100%; display: block; }}
    .media-caption {{
      padding: 6px 8px; font-size: 0.78em; color: #888;
      word-break: break-all;
    }}
    /* ── Output table ── */
    .output-table {{ width: 100%; border-collapse: collapse; font-size: 0.84em; }}
    .output-table th {{
      text-align: left; color: #888; font-weight: normal;
      padding: 4px 8px 8px; border-bottom: 1px solid #2a2a2a;
    }}
    .output-table td {{ padding: 5px 8px; border-bottom: 1px solid #1a1a1a; word-break: break-all; }}
    .output-table tr:last-child td {{ border-bottom: none; }}
    .amount {{ color: #03dac6; font-family: monospace; white-space: nowrap; }}
    /* ── Messages ── */
    .message-block {{
      background: #181818; border-left: 3px solid #bb86fc;
      padding: 10px 14px; border-radius: 0 6px 6px 0;
      margin-bottom: 8px; white-space: pre-wrap; word-break: break-word;
      font-size: 0.92em;
    }}
    /* ── Signature ── */
    .sig-mono {{ font-family: monospace; font-size: 0.78em; word-break: break-all; color: #aaa; }}
    /* ── Empty state ── */
    .empty {{ color: #555; font-size: 0.85em; font-style: italic; }}
  </style>
</head>
<body>
<nav id=""navbar"">
  <a class=""brand"" href=""/"">
    <img src=""/bitfossil.png"" alt=""bitFossil"">
    bitFossil
  </a>
  <div class=""nav-links"">
    <a href=""/"">Search</a>
    <a href=""/API"">API</a>
  </div>
</nav>
<div class=""page"">
");

            // ── Transaction header ────────────────────────────────────────────
            sb.Append($@"  <div class=""txid-header"">
    📄 Root Index: {H(txid)}
    {(signed ? @"<span class=""badge badge-signed"">✔ Signed</span>" : "")}
    {(cached ? @"<span class=""badge badge-cached"">Cached</span>" : "")}
  </div>
");

            // ── Metadata card ─────────────────────────────────────────────────
            sb.Append(@"  <div class=""section"">
    <div class=""section-title"">Transaction Info</div>
    <div class=""meta-grid"">
");
            AddMeta(sb, "Transaction ID", $@"<a href=""https://mempool.space/testnet/tx/{H(txid)}"" target=""_blank"" rel=""noopener"">{H(txid)}</a>");
            AddMeta(sb, "Blockchain", H(chainDisplayName));
            AddMeta(sb, "Block Date", H(blockDateDisplay));
            if (!string.IsNullOrEmpty(blockHeight) && blockHeight != "0")
                AddMeta(sb, "Block Height", H(blockHeight));
            if (!string.IsNullOrEmpty(confs))
                AddMeta(sb, "Confirmations", H(confs));
            if (!string.IsNullOrEmpty(totalBytes))
                AddMeta(sb, "Total Size", H(FormatBytes(totalBytes)));
            if (!string.IsNullOrEmpty(buildDate))
                AddMeta(sb, "Cached On", H(buildDate));

            sb.Append(@"    </div>
  </div>
");

            // ── Media section (images / audio / video) ────────────────────────
            var mediaFiles = files.Where(f =>
            {
                var ext = Path.GetExtension(f.name);
                return ImageExtensions.Contains(ext) || AudioExtensions.Contains(ext) || VideoExtensions.Contains(ext);
            }).ToList();

            if (mediaFiles.Count > 0)
            {
                sb.Append(@"  <div class=""section"">
    <div class=""section-title"">Media</div>
    <div class=""media-grid"">
");
                foreach (var (name, _) in mediaFiles)
                {
                    var ext = Path.GetExtension(name);
                    var url = $"/root/{txid}/{Uri.EscapeDataString(name)}";
                    sb.Append(@"      <div class=""media-card"">");
                    if (ImageExtensions.Contains(ext))
                        sb.Append($@"<a href=""{H(url)}"" target=""_blank"" rel=""noopener""><img src=""{H(url)}"" alt=""{H(name)}"" loading=""lazy""></a>");
                    else if (AudioExtensions.Contains(ext))
                        sb.Append($@"<audio controls src=""{H(url)}""></audio>");
                    else if (VideoExtensions.Contains(ext))
                        sb.Append($@"<video controls src=""{H(url)}""></video>");
                    sb.Append($@"<div class=""media-caption"">{H(name)}</div></div>
");
                }
                sb.Append(@"    </div>
  </div>
");
            }

            // ── All files ─────────────────────────────────────────────────────
            if (files.Count > 0)
            {
                sb.Append(@"  <div class=""section"">
    <div class=""section-title"">Files</div>
    <ul class=""file-list"">
");
                foreach (var (name, size) in files)
                {
                    var url  = $"/root/{txid}/{Uri.EscapeDataString(name)}";
                    var icon = GetFileIcon(name);
                    sb.Append($@"      <li>
        <span class=""file-icon"">{icon}</span>
        <span class=""file-name""><a href=""{H(url)}"" target=""_blank"" rel=""noopener"">{H(name)}</a></span>
        <span class=""file-size"">{H(FormatBytes(size.ToString()))}</span>
      </li>
");
                }
                sb.Append(@"    </ul>
  </div>
");
            }

            // ── Messages ─────────────────────────────────────────────────────
            if (messages.Count > 0)
            {
                sb.Append(@"  <div class=""section"">
    <div class=""section-title"">Messages</div>
");
                foreach (var msg in messages)
                    if (!string.IsNullOrWhiteSpace(msg))
                        sb.Append($@"    <div class=""message-block"">{H(msg)}</div>
");
                sb.Append("  </div>\n");
            }

            // ── Outputs ───────────────────────────────────────────────────────
            if (outputs.Count > 0)
            {
                sb.Append(@"  <div class=""section"">
    <div class=""section-title"">Outputs</div>
    <table class=""output-table"">
");
                sb.Append($@"      <thead><tr><th>Address</th><th>Amount ({H(chainAbbrev)})</th></tr></thead>
      <tbody>
");
                foreach (var (addr, amount) in outputs)
                    sb.Append($@"        <tr>
          <td><a href=""https://mempool.space/testnet/address/{H(addr)}"" target=""_blank"" rel=""noopener"">{H(addr)}</a></td>
          <td class=""amount"">{H(amount)}</td>
        </tr>
");
                sb.Append(@"      </tbody>
    </table>
  </div>
");
            }

            // ── Signature ─────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(signedBy) || !string.IsNullOrEmpty(signature))
            {
                sb.Append(@"  <div class=""section"">
    <div class=""section-title"">Signature</div>
    <div class=""meta-grid"">
");
                if (!string.IsNullOrEmpty(signedBy))
                    AddMeta(sb, "Signed By", $@"<a href=""https://mempool.space/testnet/address/{H(signedBy)}"" target=""_blank"" rel=""noopener"">{H(signedBy)}</a>");
                if (!string.IsNullOrEmpty(hash))
                    AddMeta(sb, "Hash", $@"<span class=""sig-mono"">{H(hash)}</span>");
                if (!string.IsNullOrEmpty(signature))
                    AddMeta(sb, "Signature", $@"<span class=""sig-mono"">{H(signature)}</span>");
                sb.Append(@"    </div>
  </div>
");
            }

            // ── Raw JSON link ─────────────────────────────────────────────────
            sb.Append($@"  <div style=""text-align:center; padding: 10px 0 20px; font-size:0.82em; color:#555;"">
    <a href=""/root/{H(txid)}/ROOT.json"">View raw ROOT.json</a>
  </div>
");

            sb.Append("</div>\n</body>\n</html>");
            return sb.ToString();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string H(string s) => HttpUtility.HtmlEncode(s ?? "");

        private static string GetString(JsonElement el, string property)
        {
            if (el.TryGetProperty(property, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.String)  return prop.GetString() ?? "";
                if (prop.ValueKind == JsonValueKind.Number)  return prop.GetRawText();
                if (prop.ValueKind == JsonValueKind.True)    return "true";
                if (prop.ValueKind == JsonValueKind.False)   return "false";
            }
            return "";
        }

        private static void AddMeta(StringBuilder sb, string label, string valueHtml)
        {
            sb.Append($"      <span class=\"meta-label\">{HttpUtility.HtmlEncode(label)}:</span><span class=\"meta-value\">{valueHtml}</span>\n");
        }

        private static string FormatBytes(string rawBytes)
        {
            if (!long.TryParse(rawBytes, out long bytes)) return rawBytes;
            if (bytes < 1024)        return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024):F2} MB";
        }

        private static string GetFileIcon(string name)
        {
            var ext = Path.GetExtension(name);
            if (ImageExtensions.Contains(ext))  return "🖼️";
            if (AudioExtensions.Contains(ext))  return "🎵";
            if (VideoExtensions.Contains(ext))  return "🎬";
            if (PdfExtensions.Contains(ext))    return "📄";
            if (TextExtensions.Contains(ext))   return "📝";
            return "📎";
        }

        // Infer the chain abbreviation from the leading characters of a P2PKH address.
        private static string DetectChain(string address)
        {
            if (string.IsNullOrEmpty(address)) return "BTC";
            if (address.StartsWith("D"))                   return "DOG";
            if (address.StartsWith("L"))                   return "LTC";
            if (address.StartsWith("M"))                   return "MZC";
            if (address.StartsWith("m") || address.StartsWith("n") ||
                address.StartsWith("2") || address.StartsWith("tb1")) return "TBTC";
            return "BTC";
        }

        private static string ChainDisplayName(string abbrev) => abbrev switch
        {
            "DOG"  => "Dogecoin (DOG)",
            "LTC"  => "Litecoin (LTC)",
            "MZC"  => "Mazacoin (MZC)",
            "TBTC" => "Bitcoin Testnet (TBTC)",
            _      => "Bitcoin (BTC)",
        };
    }
}
