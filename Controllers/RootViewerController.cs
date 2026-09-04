using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
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
        private readonly IMemoryCache _cache;

        // Rendered HTML for a given txid/address rarely changes (on-chain data is immutable
        // once written).  A 5-minute TTL keeps the page fresh while eliminating redundant
        // File.ReadAllText + StringBuilder allocations for every duplicate page view.
        private static readonly TimeSpan HtmlCacheTtl = TimeSpan.FromMinutes(5);

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg" };
        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".wav", ".ogg", ".flac", ".aac", ".m4a" };
        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".m4v", ".webm", ".ogv", ".mov" };
        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".txt", ".xml", ".csv", ".md" };
        private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".pdf" };

        public RootViewerController(Wrapper wrapper, IMemoryCache cache)
        {
            _wrapper = wrapper;
            _cache = cache;
        }

        // Single dispatcher: handles both /root/{txid} (64-char hex) and /root/{address} (26-34 char base58)
        [HttpGet("{value}")]
        [HttpGet("{value}/index.htm")]
        public IActionResult Get(string value)
        {
            string htmlCacheKey = $"root-html:{value}";

            // 64-char hex → transaction root page
            if (Regex.IsMatch(value, @"^[0-9a-fA-F]{64}$"))
            {
                if (_cache.TryGetValue(htmlCacheKey, out string? cachedHtml) && cachedHtml != null)
                    return Content(cachedHtml, "text/html; charset=utf-8");

                var rootJsonPath = Path.Combine(_wrapper.RootPath, value, "ROOT.json");
                if (!System.IO.File.Exists(rootJsonPath))
                    return NotFound();

                string json;
                try { json = System.IO.File.ReadAllText(rootJsonPath, Encoding.UTF8); }
                catch { return NotFound(); }

                JsonElement root;
                try { root = JsonSerializer.Deserialize<JsonElement>(json); }
                catch { return Content("<html><body>Error parsing ROOT.json</body></html>", "text/html"); }

                string html = BuildHtml(value, root);
                _cache.Set(htmlCacheKey, html, new MemoryCacheEntryOptions()
                    .SetSize(1)
                    .SetAbsoluteExpiration(HtmlCacheTtl));
                return Content(html, "text/html; charset=utf-8");
            }

            // base58 address → object and/or profile page
            if (Regex.IsMatch(value, @"^[a-zA-Z0-9][a-km-zA-HJ-NP-Z1-9]{25,33}$"))
            {
                if (_cache.TryGetValue(htmlCacheKey, out string? cachedHtml) && cachedHtml != null)
                    return Content(cachedHtml, "text/html; charset=utf-8");

                // Try loading OBJ.json
                JsonElement? obj = null;
                var objJsonPath = Path.Combine(_wrapper.RootPath, value, "OBJ.json");
                if (System.IO.File.Exists(objJsonPath))
                {
                    try
                    {
                        var objJson = System.IO.File.ReadAllText(objJsonPath, Encoding.UTF8);
                        var objElement = JsonSerializer.Deserialize<JsonElement>(objJson);
                        if (objElement.ValueKind == JsonValueKind.Object)
                            obj = objElement;
                        else if (objElement.ValueKind == JsonValueKind.Array && objElement.GetArrayLength() > 0)
                            obj = objElement[0];
                    }
                    catch { /* ignore parse errors; obj stays null */ }
                }

                // Try loading GetProfileByAddress.json
                JsonElement? profile = null;
                var profileJsonPath = Path.Combine(_wrapper.RootPath, value, "GetProfileByAddress.json");
                if (System.IO.File.Exists(profileJsonPath))
                {
                    try
                    {
                        var profileJson = System.IO.File.ReadAllText(profileJsonPath, Encoding.UTF8);
                        var profileElement = JsonSerializer.Deserialize<JsonElement>(profileJson);
                        if (profileElement.ValueKind == JsonValueKind.Object)
                            profile = profileElement;
                        else if (profileElement.ValueKind == JsonValueKind.Array && profileElement.GetArrayLength() > 0)
                            profile = profileElement[0];
                    }
                    catch { /* ignore parse errors; profile stays null */ }
                }

                if (obj == null && profile == null)
                    return NotFound();

                string html = BuildAddressHtml(value, obj, profile);
                _cache.Set(htmlCacheKey, html, new MemoryCacheEntryOptions()
                    .SetSize(1)
                    .SetAbsoluteExpiration(HtmlCacheTtl));
                return Content(html, "text/html; charset=utf-8");
            }

            return NotFound();
        }

        // Convert IPFS:CID\filename or IPFS:CID/filename to https://ipfs.io/ipfs/CID
        private static string IpfsToGatewayUrl(string urn)
        {
            if (string.IsNullOrWhiteSpace(urn)) return "";
            var normalized = urn.Replace('\\', '/');
            var idx = normalized.IndexOf("IPFS:", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return urn;
            var raw = normalized.Substring(idx + 5);
            var cid = raw.Split('/')[0].Trim();
            return string.IsNullOrEmpty(cid) ? "" : $"https://ipfs.io/ipfs/{cid}";
        }

        // Resolve a profile/object Image field to a displayable URL.
        // Handles: IPFS:CID/file → ipfs.io, on-chain txid/file or CHAIN:txid/file → /root/txid/file,
        // and direct https:// URLs. Returns "" if unresolvable.
        private static string ProfileImageUrl(string image)
        {
            if (string.IsNullOrWhiteSpace(image)) return "";
            var v = image.Replace('\\', '/').Trim();

            // Direct https / data URL
            if (v.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                v.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return v;

            // IPFS: IPFS:CID/filename
            if (v.StartsWith("IPFS:", StringComparison.OrdinalIgnoreCase))
            {
                var raw = v.Substring(5);
                var cid = raw.Split('/')[0].Trim();
                return string.IsNullOrEmpty(cid) ? "" : $"https://ipfs.io/ipfs/{cid}";
            }

            // On-chain: CHAIN:txid/filename  or  txid/filename  (64-char hex prefix)
            // Strip optional chain prefix (BTC:, LTC:, DOG:, MZC:)
            var stripped = Regex.Replace(v, @"^(BTC|LTC|DOG|MZC):", "", RegexOptions.IgnoreCase);
            var slashIdx = stripped.IndexOf('/');
            if (slashIdx > 0)
            {
                var txPart = stripped[..slashIdx];
                var filePart = stripped[(slashIdx + 1)..];
                if (Regex.IsMatch(txPart, @"^[0-9a-fA-F]{64}$") && !string.IsNullOrEmpty(filePart))
                    return $"/root/{txPart}/{Uri.EscapeDataString(filePart)}";
            }

            return "";
        }

        // Return the display filename from an IPFS URN (e.g. ATLAS.mp4)
        private static string IpfsFilename(string urn)
        {
            if (string.IsNullOrWhiteSpace(urn)) return "";
            var normalized = urn.Replace('\\', '/');
            var idx = normalized.IndexOf("IPFS:", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";
            var raw = normalized.Substring(idx + 5);
            var parts = raw.Split('/');
            return parts.Length > 1 ? parts[1].Trim() : "";
        }

        // ── BuildAddressHtml: top-level wrapper for address pages ──────────────
        // Renders a profile card (if profile JSON exists) followed by object
        // details (if OBJ.json exists). Either or both may be present.
        private string BuildAddressHtml(string address, JsonElement? obj, JsonElement? profile)
        {
            if (obj == null && profile != null)
            {
                // Profile-only page: full page built inside BuildProfileOnlyHtml
                return BuildProfileOnlyHtml(address, profile.Value);
            }

            if (obj != null && profile == null)
            {
                // Object-only (no profile): existing behaviour
                return BuildObjectHtml(address, obj.Value);
            }

            // Both exist: inject profile card into object page HTML (before <div class="page"> content)
            string objectHtml = BuildObjectHtml(address, obj!.Value);
            string profileCard = BuildProfileCardHtml(address, profile!.Value);

            // Insert the profile card right after <div class="page"> (and the obj-header that follows)
            // Strategy: splice in after the opening <div class="page"> tag
            const string pageDiv = "<div class=\"page\">";
            int pageIdx = objectHtml.IndexOf(pageDiv, StringComparison.Ordinal);
            if (pageIdx >= 0)
            {
                int insertAt = pageIdx + pageDiv.Length + 1; // +1 for the newline
                return objectHtml[..insertAt] + profileCard + objectHtml[insertAt..];
            }

            // Fallback: prepend card before object HTML
            return profileCard + objectHtml;
        }

        // Full standalone page for addresses that have a profile but no OBJ.json
        private string BuildProfileOnlyHtml(string address, JsonElement profile)
        {
            string displayName = GetString(profile, "DisplayName");
            string urn         = GetString(profile, "URN");
            string image       = GetString(profile, "Image");
            string pageTitle   = !string.IsNullOrEmpty(displayName) ? $"{H(displayName)} – bitFossil"
                               : !string.IsNullOrEmpty(urn)         ? $"@{H(urn)} – bitFossil"
                               : $"{H(address[..6])}… – bitFossil";
            string chainAbbrev = DetectChain(address);
            string chainDisplay = ChainDisplayName(chainAbbrev);
            string avatarUrl   = ProfileImageUrl(image);
            string ogImage     = string.IsNullOrEmpty(avatarUrl) ? "https://p2fk.io/bitfossil.png" : avatarUrl;
            string profileCard = BuildProfileCardHtml(address, profile);

            var sb = new StringBuilder();
            sb.Append($@"<!DOCTYPE html>
<html lang=""en"">
<head>
  <meta charset=""UTF-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
  <title>{pageTitle}</title>
  <link rel=""apple-touch-icon"" sizes=""180x180"" href=""/apple-touch-icon.png"">
  <link rel=""icon"" type=""image/png"" sizes=""32x32"" href=""/favicon-32x32.png"">
  <link rel=""icon"" type=""image/png"" sizes=""16x16"" href=""/favicon-16x16.png"">
  <meta property=""og:title"" content=""{pageTitle}"">
  <meta property=""og:image"" content=""{H(ogImage)}"">
  <meta property=""og:url"" content=""https://p2fk.io/root/{H(address)}"">
  <meta property=""og:type"" content=""profile"">
  <meta property=""og:site_name"" content=""bitFossil"">
  <meta name=""twitter:card"" content=""summary"">
  <meta name=""twitter:title"" content=""{pageTitle}"">
  <meta name=""twitter:image"" content=""{H(ogImage)}"">
  <meta name=""twitter:site"" content=""@bitFossil"">
  <style>
    *, *::before, *::after {{ box-sizing: border-box; }}
    html {{ scroll-behavior: smooth; }}
    body {{ background: #121212; color: #e0e0e0; font-family: Arial, sans-serif; margin: 0; padding: 0; min-height: 100vh; }}
    a {{ color: #03dac6; text-decoration: none; }}
    a:hover {{ color: #bb86fc; }}
    #navbar {{
      position: fixed; top: 0; left: 0; right: 0; z-index: 2000;
      background: #1e1e1e; border-bottom: 2px solid #bb86fc;
      display: flex; align-items: center; gap: 10px; padding: 7px 14px; height: 54px;
    }}
    #navbar .brand {{ display: flex; align-items: center; gap: 8px; flex-shrink: 0; text-decoration: none; color: #bb86fc; font-size: 1.15em; font-weight: bold; }}
    #navbar .brand img {{ width: 36px; height: 36px; border-radius: 4px; object-fit: cover; }}
    .nav-links {{ display: flex; align-items: center; gap: 12px; flex-shrink: 0; margin-left: auto; }}
    .nav-links a {{ color: #b0b0b0; font-size: 0.82em; white-space: nowrap; padding: 4px 8px; border-radius: 4px; border: 1px solid #333; transition: all 0.2s; }}
    .nav-links a:hover {{ background: #2a2a2a; color: #bb86fc; border-color: #bb86fc; }}
    .page {{ max-width: 960px; margin: 0 auto; padding: 74px 16px 40px; }}
    {ProfileCardCss()}
  </style>
</head>
<body>
<nav id=""navbar"">
  <a class=""brand"" href=""/""><img src=""/bitfossil.png"" alt=""bitFossil"">bitFossil</a>
  <div class=""nav-links""><a href=""/"">Search</a><a href=""/API"">API</a></div>
</nav>
<div class=""page"">
{profileCard}</div>
</body>
</html>");
            return sb.ToString();
        }

        // Returns the CSS block shared by the profile card (inlined into both page types)
        private static string ProfileCardCss() => @"
    .profile-panel { background: #1e1e1e; border-radius: 8px; border: 1px solid #2a2a2a; padding: 16px; margin-bottom: 18px; display: flex; gap: 16px; align-items: flex-start; }
    .profile-panel-img { width: 88px; height: 88px; border-radius: 8px; object-fit: cover; background: #333; flex-shrink: 0; }
    .profile-panel-info { flex: 1; min-width: 0; overflow: hidden; }
    .profile-panel-info h2 { margin: 0 0 4px; color: #bb86fc; font-size: 1.1em; overflow-wrap: break-word; word-break: break-word; }
    .profile-panel-info p { margin: 2px 0; font-size: 0.85em; color: #b0b0b0; overflow-wrap: break-word; word-break: break-word; }
    .profile-handle-addr { font-family: monospace; font-size: 0.65em; color: #666; font-weight: normal; vertical-align: middle; }
    .profile-url-link { display: inline-block; border: 1px solid #bb86fc; border-radius: 10px; padding: 1px 8px; font-size: 0.75em; color: #bb86fc; margin: 2px; transition: all 0.2s; }
    .profile-url-link:hover { background: #bb86fc; color: #121212; }
    .profile-url-link.internal { border-color: #03dac6; color: #03dac6; }
    .profile-url-link.internal:hover { background: #03dac6; color: #121212; }
    .profile-height { font-size: 0.78em; color: #888; margin-top: 4px; }
    .profile-height span { color: #03dac6; font-weight: bold; }
    @media(max-width:600px) { .profile-panel { flex-direction: column; } .profile-panel-img { width: 64px; height: 64px; } }
";

        // Renders the profile card HTML snippet (no <html>/<head> — embeds into any page)
        private string BuildProfileCardHtml(string address, JsonElement profile)
        {
            string displayName  = GetString(profile, "DisplayName");
            string urn          = GetString(profile, "URN");
            string bio          = GetString(profile, "Bio");
            string image        = GetString(profile, "Image");
            string createdDate  = GetString(profile, "CreatedDate");
            string changeDate   = GetString(profile, "ChangeDate");
            string processHeight = GetString(profile, "ProcessHeight");

            string headingName = !string.IsNullOrEmpty(displayName) ? displayName
                               : !string.IsNullOrEmpty(urn)         ? $"@{urn}"
                               : address;
            string shortAddr   = address.Length > 12 ? address[..6] + "…" + address[^6..] : address;

            // ── Avatar image URL ───────────────────────────────────────────────
            string avatarUrl = ProfileImageUrl(image);
            string chainAbbrev = DetectChain(address);

            // ── Location ──────────────────────────────────────────────────────
            string locText = "";
            if (profile.TryGetProperty("Location", out var locProp))
            {
                if (locProp.ValueKind == JsonValueKind.Object)
                {
                    if (locProp.TryGetProperty("quark", out var q)) locText = q.GetString() ?? "";
                    else if (locProp.TryGetProperty("Quark", out var q2)) locText = q2.GetString() ?? "";
                }
                else if (locProp.ValueKind == JsonValueKind.String)
                    locText = locProp.GetString() ?? "";
            }

            // ── URL links ─────────────────────────────────────────────────────
            var urlLinks = new List<(string label, string value)>();
            if (profile.TryGetProperty("URL", out var urlProp) && urlProp.ValueKind == JsonValueKind.Object)
                foreach (var entry in urlProp.EnumerateObject())
                    urlLinks.Add((entry.Name, entry.Value.GetString() ?? ""));

            // ── Build HTML ────────────────────────────────────────────────────
            var sb = new StringBuilder();
            sb.Append(@"<div class=""profile-panel"">
");

            // Avatar
            if (!string.IsNullOrEmpty(avatarUrl))
            {
                // Use lazy JS loading for the image (same pattern as object media)
                sb.Append($@"  <img class=""profile-panel-img"" id=""prof-avatar"" src=""/bitfossil.png"" alt=""{H(headingName)}"">
");
            }
            else
            {
                sb.Append($@"  <img class=""profile-panel-img"" src=""/bitfossil.png"" alt=""{H(headingName)}"">
");
            }

            sb.Append(@"  <div class=""profile-panel-info"">
");
            // Name + address
            sb.Append($@"    <h2>{H(headingName)} <span class=""profile-handle-addr"">{H(shortAddr)}</span></h2>
");

            // Bio
            if (!string.IsNullOrEmpty(bio))
                sb.Append($@"    <p>{H(bio)}</p>
");

            // Location
            if (!string.IsNullOrEmpty(locText))
                sb.Append($@"    <p>📍 {H(locText)}</p>
");

            // URL links
            if (urlLinks.Count > 0)
            {
                sb.Append("    <p>🔗 ");
                foreach (var (label, val) in urlLinks)
                {
                    if (val.StartsWith("@"))
                    {
                        // Internal profile link — link to index.html profile view
                        var urnTarget = val.TrimStart('@');
                        sb.Append($@"<a class=""profile-url-link internal"" href=""/?profile={H(Uri.EscapeDataString(urnTarget))}"">@{H(urnTarget)}</a>");
                    }
                    else if (val.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             val.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append($@"<a class=""profile-url-link"" href=""{H(val)}"" target=""_blank"" rel=""noopener"">{H(label)}</a>");
                    }
                    else if (!string.IsNullOrWhiteSpace(val))
                    {
                        sb.Append($@"<span class=""profile-url-link"">{H(label)}: {H(val)}</span>");
                    }
                }
                sb.Append("</p>\n");
            }

            // Dates
            if (!string.IsNullOrEmpty(createdDate) && DateTime.TryParse(createdDate, out var cdt)
                && cdt.Year > 1)
                sb.Append($@"    <p>Joined: {H(cdt.ToString("yyyy-MM-dd"))}</p>
");
            if (!string.IsNullOrEmpty(changeDate) && DateTime.TryParse(changeDate, out var chd)
                && chd.Year > 1)
                sb.Append($@"    <p>Modified: {H(chd.ToString("yyyy-MM-dd"))}</p>
");

            // Process height
            if (!string.IsNullOrEmpty(processHeight) && processHeight != "0")
                sb.Append($@"    <p class=""profile-height"">Process height: <span>{H(processHeight)}</span></p>
");

            // URN link
            if (!string.IsNullOrEmpty(urn))
                sb.Append($@"    <p><a href=""/?profile={H(Uri.EscapeDataString(urn))}"">View full profile →</a></p>
");

            sb.Append(@"  </div>
</div>
");

            // Lazy-load avatar via JS
            if (!string.IsNullOrEmpty(avatarUrl))
            {
                // Avatar may be IPFS (external) or on-chain (/root/... - same origin)
                bool isCrossOrigin = avatarUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                sb.Append($@"<script>
(function(){{
  var img=document.getElementById('prof-avatar');
  if(!img) return;
  var url='{H(avatarUrl)}';
  var el=new Image();
  {(isCrossOrigin ? "el.crossOrigin='Anonymous';" : "")}
  el.onload=function(){{img.src=url;}};
  el.onerror=function(){{}};
  el.src=url;
}})();
</script>
");
            }

            return sb.ToString();
        }

        private string BuildObjectHtml(string address, JsonElement obj)
        {
            // ── Extract fields ────────────────────────────────────────────────
            string name        = GetString(obj, "Name");
            string description = GetString(obj, "Description");
            string urn         = GetString(obj, "URN");
            string image       = GetString(obj, "Image");
            string uri         = GetString(obj, "URI");
            string license     = GetString(obj, "License");
            string maximum      = GetString(obj, "Maximum");
            string txid         = GetString(obj, "TransactionId");
            string createdDate  = GetString(obj, "CreatedDate");
            string changeDate   = GetString(obj, "ChangeDate");
            string blockHeight  = GetString(obj, "BlockHeight");
            string buildDate    = GetString(obj, "BuildDate");

            if (string.IsNullOrEmpty(name)) name = address;

            // ── Detect chain from address prefix ──────────────────────────────
            string chainAbbrev      = DetectChain(address);
            string chainDisplayName = ChainDisplayName(chainAbbrev);

            // ── IPFS media URLs ───────────────────────────────────────────────
            string imageGatewayUrl = IpfsToGatewayUrl(image);
            string urnGatewayUrl   = IpfsToGatewayUrl(urn);
            string urnFilename     = IpfsFilename(urn);
            string imageFilename   = IpfsFilename(image);

            // Determine file extension for the URN artifact
            string urnExt = string.IsNullOrEmpty(urnFilename)
                ? Path.GetExtension(urnGatewayUrl)
                : Path.GetExtension(urnFilename);

            // ── OG preview image: prefer object image, else bitfossil logo ────
            string ogImage = string.IsNullOrEmpty(imageGatewayUrl)
                ? "https://p2fk.io/bitfossil.png"
                : imageGatewayUrl;

            // ── Creators ──────────────────────────────────────────────────────
            var creators = new List<(string addr, string date)>();
            if (obj.TryGetProperty("Creators", out var creatorsProp))
            {
                if (creatorsProp.ValueKind == JsonValueKind.Object)
                    foreach (var c in creatorsProp.EnumerateObject())
                        creators.Add((c.Name, c.Value.ValueKind == JsonValueKind.String ? c.Value.GetString() ?? "" : ""));
                else if (creatorsProp.ValueKind == JsonValueKind.Array)
                    foreach (var c in creatorsProp.EnumerateArray())
                        if (c.GetString() is string s) creators.Add((s, ""));
            }

            // ── Owners ────────────────────────────────────────────────────────
            var owners = new List<(string addr, int qty, string lastTx)>();
            if (obj.TryGetProperty("Owners", out var ownersProp) && ownersProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var o in ownersProp.EnumerateObject())
                {
                    int qty = 0;
                    string lastTx = "";
                    if (o.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (o.Value.TryGetProperty("Item1", out var item1) && item1.ValueKind == JsonValueKind.Number)
                            item1.TryGetInt32(out qty);
                        if (o.Value.TryGetProperty("Item2", out var item2) && item2.ValueKind == JsonValueKind.String)
                            lastTx = item2.GetString() ?? "";
                    }
                    owners.Add((o.Name, qty, lastTx));
                }
            }

            // ── Edition size = sum of all owner qty values ────────────────────
            long editionSize = owners.Count > 0 ? owners.Sum(o => (long)o.qty) : 0;
            string editionSizeDisplay = editionSize > 0 ? editionSize.ToString("N0") : "0";

            // ── Listings ──────────────────────────────────────────────────────
            var listings = new List<(string seller, int qty, double value, string requestor, string listingDate)>();
            if (obj.TryGetProperty("Listings", out var listingsProp) && listingsProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var l in listingsProp.EnumerateObject())
                {
                    int qty = 0;
                    double value = 0;
                    string requestor = ""; // parsed for completeness; not currently displayed
                    string listingDate = "";
                    if (l.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (l.Value.TryGetProperty("Qty", out var qtyProp) && qtyProp.ValueKind == JsonValueKind.Number)
                            qtyProp.TryGetInt32(out qty);
                        if (l.Value.TryGetProperty("Value", out var valProp) && valProp.ValueKind == JsonValueKind.Number)
                            valProp.TryGetDouble(out value);
                        if (l.Value.TryGetProperty("Requestor", out var rProp) && rProp.ValueKind == JsonValueKind.String)
                            requestor = rProp.GetString() ?? "";
                        if (l.Value.TryGetProperty("BlockDate", out var bdProp) && bdProp.ValueKind == JsonValueKind.String)
                            listingDate = bdProp.GetString() ?? "";
                    }
                    listings.Add((l.Name, qty, value, requestor, listingDate));
                }
            }

            // ── Attributes ────────────────────────────────────────────────────
            var attributes = new List<(string traitType, string value)>();
            if (obj.TryGetProperty("Attributes", out var attrProp) && attrProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in attrProp.EnumerateArray())
                {
                    string traitType = "";
                    string attrVal = "";
                    if (a.TryGetProperty("trait_type", out var tt)) traitType = tt.GetString() ?? "";
                    if (a.TryGetProperty("value", out var av)) attrVal = av.GetString() ?? av.GetRawText();
                    attributes.Add((traitType, attrVal));
                }
            }

            var sb = new StringBuilder();

            // ─────────────────────────────────────────────────────────────────
            // HTML head
            // ─────────────────────────────────────────────────────────────────
            string shortAddr = address.Length > 12 ? address[..6] + "…" + address[^6..] : address;
            string pageTitle = $"{H(name)} – bitFossil";
            string ogDesc = string.IsNullOrEmpty(description)
                ? $"P2FK digital object on {H(chainDisplayName)}"
                : H(description.Length > 200 ? description[..200] + "…" : description);

            sb.Append($@"<!DOCTYPE html>
<html lang=""en"">
<head>
  <meta charset=""UTF-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
  <title>{pageTitle}</title>
  <link rel=""apple-touch-icon"" sizes=""180x180"" href=""/apple-touch-icon.png"">
  <link rel=""icon"" type=""image/png"" sizes=""32x32"" href=""/favicon-32x32.png"">
  <link rel=""icon"" type=""image/png"" sizes=""16x16"" href=""/favicon-16x16.png"">
  <meta property=""og:title"" content=""{pageTitle}"">
  <meta property=""og:description"" content=""{ogDesc}"">
  <meta property=""og:image"" content=""{H(ogImage)}"">
  <meta property=""og:url"" content=""https://p2fk.io/root/{H(address)}"">
  <meta property=""og:type"" content=""article"">
  <meta property=""og:site_name"" content=""bitFossil"">
  <meta name=""twitter:card"" content=""summary_large_image"">
  <meta name=""twitter:title"" content=""{pageTitle}"">
  <meta name=""twitter:description"" content=""{ogDesc}"">
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
    /* ── Profile card (shown when GetProfileByAddress.json also exists) ── */
    {ProfileCardCss()}
    /* ── Content ── */
    .page {{ max-width: 960px; margin: 0 auto; padding: 74px 16px 40px; }}
    .obj-header {{ word-break: break-all; color: #bb86fc; font-size: 1.4em; font-weight: bold; margin-bottom: 4px; }}
    .section {{ background: #1e1e1e; border-radius: 8px; padding: 16px 20px; margin-bottom: 18px; }}
    .section-title {{
      font-size: 0.78em; font-weight: bold; text-transform: uppercase;
      letter-spacing: 0.08em; color: #bb86fc; margin-bottom: 12px;
    }}
    .description {{
      white-space: pre-wrap; word-break: break-word;
      font-size: 0.92em; line-height: 1.6;
    }}
    .meta-grid {{ display: grid; grid-template-columns: max-content 1fr; gap: 4px 14px; font-size: 0.88em; }}
    .meta-label {{ color: #888; white-space: nowrap; }}
    .meta-value {{ word-break: break-all; }}
    /* ── Media ── */
    .media-hero {{
      display: flex; justify-content: center; align-items: center;
      background: #000; border-radius: 8px; overflow: hidden;
      margin-bottom: 18px; min-height: 120px; position: relative;
    }}
    .media-hero img {{
      max-width: 100%; max-height: 480px;
      object-fit: contain; display: block;
    }}
    .media-hero video, .media-hero audio {{ max-width: 100%; }}
    .media-spinner {{
      position: absolute; font-size: 2em; color: #555; animation: spin 1.2s linear infinite;
    }}
    @keyframes spin {{ to {{ transform: rotate(360deg); }} }}
    /* ── Thumbnail + artifact ── */
    .media-pair {{
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
      gap: 14px; margin-bottom: 4px;
    }}
    .media-card {{
      background: #121212; border-radius: 6px; overflow: hidden;
      border: 1px solid #2a2a2a;
    }}
    .media-card img {{ width: 100%; display: block; max-height: 300px; object-fit: contain; background: #000; }}
    .media-card video, .media-card audio {{ width: 100%; display: block; }}
    .media-caption {{ padding: 6px 8px; font-size: 0.78em; color: #888; word-break: break-all; }}
    /* ── Tables ── */
    .data-table {{ width: 100%; border-collapse: collapse; font-size: 0.84em; }}
    .data-table th {{
      text-align: left; color: #888; font-weight: normal;
      padding: 4px 8px 8px; border-bottom: 1px solid #2a2a2a;
    }}
    .data-table td {{ padding: 5px 8px; border-bottom: 1px solid #1a1a1a; word-break: break-all; }}
    .data-table tr:last-child td {{ border-bottom: none; }}
    .amount {{ color: #03dac6; font-family: monospace; white-space: nowrap; }}
    .tx-mono {{ font-family: monospace; font-size: 0.78em; color: #aaa; }}
    /* ── Attribute chips ── */
    .attr-grid {{ display: flex; flex-wrap: wrap; gap: 8px; }}
    .attr-chip {{
      background: #2a2a2a; border-radius: 6px; padding: 6px 12px;
      font-size: 0.82em;
    }}
    .attr-chip .trait {{ color: #888; font-size: 0.85em; display: block; margin-bottom: 2px; }}
    .attr-chip .val {{ color: #e0e0e0; }}
    /* ── Loading state ── */
    .ipfs-loading {{ text-align:center; padding: 32px; color: #555; font-size: 0.9em; }}
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

            // ── Object header ─────────────────────────────────────────────────
            sb.Append($@"  <div class=""obj-header"">🎨 {H(name)}</div>
  <div class=""obj-address"">Address: {H(address)} &nbsp;·&nbsp; {H(chainDisplayName)}</div>
");

            // ── Media section ─────────────────────────────────────────────────
            bool hasArtifact = !string.IsNullOrEmpty(urnGatewayUrl);
            bool hasThumbnail = !string.IsNullOrEmpty(imageGatewayUrl) && imageGatewayUrl != urnGatewayUrl;

            if (hasArtifact || hasThumbnail)
            {
                sb.Append(@"  <div class=""section"">
    <div class=""section-title"">Media</div>
    <div class=""media-pair"">
");
                // Thumbnail / cover image
                if (hasThumbnail)
                {
                    sb.Append($@"      <div class=""media-card"">
        <img id=""obj-thumb"" src="""" alt=""{H(imageFilename.Length > 0 ? imageFilename : "thumbnail")}"" loading=""lazy"">
        <div class=""media-caption"">{H(imageFilename.Length > 0 ? imageFilename : "Cover Image")}</div>
      </div>
");
                }

                // Primary artifact
                if (hasArtifact)
                {
                    sb.Append(@"      <div class=""media-card"" id=""artifact-card"">
        <div class=""ipfs-loading"">⏳ Loading artifact…</div>
      </div>
");
                }

                sb.Append(@"    </div>
  </div>
");
            }
            else if (!string.IsNullOrEmpty(imageGatewayUrl))
            {
                // Only one image (same as URN), display as hero
                sb.Append($@"  <div class=""media-hero"">
    <img id=""obj-thumb"" src="""" alt=""{H(name)}"" loading=""lazy"">
  </div>
");
            }

            // ── Description ───────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(description))
            {
                sb.Append(@"  <div class=""section"">
    <div class=""section-title"">Description</div>
");
                sb.Append($@"    <div class=""description"">{H(description)}</div>
  </div>
");
            }

            // ── Object metadata ───────────────────────────────────────────────
            sb.Append(@"  <div class=""section"">
    <div class=""section-title"">Object Info</div>
    <div class=""meta-grid"">
");
            AddMeta(sb, "Object Address", H(address));
            AddMeta(sb, "Blockchain", H(chainDisplayName));
            AddMeta(sb, "Edition Size", editionSizeDisplay);
            if (!string.IsNullOrEmpty(blockHeight))
                AddMeta(sb, "Block Height", H(blockHeight));
            if (!string.IsNullOrEmpty(license))
                AddMeta(sb, "License", H(license));
            if (!string.IsNullOrEmpty(createdDate) && DateTime.TryParse(createdDate, out var cd))
                AddMeta(sb, "Created", H(cd.ToString("yyyy-MM-dd HH:mm:ss") + " UTC"));
            if (!string.IsNullOrEmpty(changeDate) && DateTime.TryParse(changeDate, out var chd))
                AddMeta(sb, "Last Updated", H(chd.ToString("yyyy-MM-dd HH:mm:ss") + " UTC"));
            if (!string.IsNullOrEmpty(buildDate))
                AddMeta(sb, "Cached On", H(buildDate));
            if (!string.IsNullOrEmpty(txid))
                AddMeta(sb, "Mint Transaction", $@"<a href=""{H(ExplorerTxUrl(chainAbbrev, txid))}"" target=""_blank"" rel=""noopener"" class=""tx-mono"">{H(txid)}</a>");
            if (!string.IsNullOrEmpty(uri))
                AddMeta(sb, "External URI", $@"<a href=""{H(uri)}"" target=""_blank"" rel=""noopener"">{H(uri)}</a>");
            if (!string.IsNullOrEmpty(urn))
                AddMeta(sb, "Artifact URN", H(urn));
            if (!string.IsNullOrEmpty(image))
                AddMeta(sb, "Image URN", H(image));
            sb.Append(@"    </div>
  </div>
");

            // ── Attributes ────────────────────────────────────────────────────
            if (attributes.Count > 0)
            {
                sb.Append(@"  <div class=""section"">
    <div class=""section-title"">Attributes</div>
    <div class=""attr-grid"">
");
                foreach (var (traitType, attrVal) in attributes)
                    sb.Append($@"      <div class=""attr-chip""><span class=""trait"">{H(traitType)}</span><span class=""val"">{H(attrVal)}</span></div>
");
                sb.Append(@"    </div>
  </div>
");
            }

            // ── Creators ─────────────────────────────────────────────────────
            if (creators.Count > 0)
            {
                sb.Append(@"  <div class=""section"">
    <div class=""section-title"">Creators</div>
    <table class=""data-table"">
      <thead><tr><th>Address</th><th>Date</th></tr></thead>
      <tbody>
");
                foreach (var (c, cDate) in creators)
                {
                    string dateDisplay = "";
                    if (!string.IsNullOrEmpty(cDate))
                    {
                        bool isNullDate = cDate.StartsWith("0001-01-01") || cDate.StartsWith("1970-01-01");
                        if (isNullDate)
                            dateDisplay = "<span style=\"color:#666;font-style:italic;\">Pending</span>";
                        else if (DateTime.TryParse(cDate, out var cdt))
                            dateDisplay = H(cdt.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
                        else
                            dateDisplay = H(cDate);
                    }
                    sb.Append($@"        <tr>
          <td><a href=""{H(ExplorerAddressUrl(chainAbbrev, c))}"" target=""_blank"" rel=""noopener"">{H(c)}</a></td>
          <td>{dateDisplay}</td>
        </tr>
");
                }
                sb.Append(@"      </tbody>
    </table>
  </div>
");
            }

            // ── Owners ────────────────────────────────────────────────────────
            if (owners.Count > 0)
            {
                sb.Append(@"  <div class=""section"">
    <div class=""section-title"">Owners</div>
    <table class=""data-table"">
      <thead><tr><th>Address</th><th>Qty</th><th>Last Transaction</th></tr></thead>
      <tbody>
");
                foreach (var (ownerAddr, qty, lastTx) in owners)
                {
                    string txHtml = string.IsNullOrEmpty(lastTx) ? "" :
                        $@"<a href=""{H(ExplorerTxUrl(chainAbbrev, lastTx))}"" target=""_blank"" rel=""noopener"" class=""tx-mono"">{H(lastTx[..8])}…{H(lastTx[^8..])}</a>";
                    sb.Append($@"        <tr>
          <td><a href=""{H(ExplorerAddressUrl(chainAbbrev, ownerAddr))}"" target=""_blank"" rel=""noopener"">{H(ownerAddr)}</a></td>
          <td class=""amount"">{qty}</td>
          <td>{txHtml}</td>
        </tr>
");
                }
                sb.Append(@"      </tbody>
    </table>
  </div>
");
            }

            // ── Listings ─────────────────────────────────────────────────────
            if (listings.Count > 0)
            {
                sb.Append(@"  <div class=""section"">
    <div class=""section-title"">Listings</div>
    <table class=""data-table"">
      <thead><tr><th>Seller</th><th>Qty</th><th>Price</th><th>Listed</th></tr></thead>
      <tbody>
");
                foreach (var (seller, qty, value, _, listingDate) in listings)
                {
                    string listedHtml = "";
                    if (!string.IsNullOrEmpty(listingDate) && DateTime.TryParse(listingDate, out var ld))
                        listedHtml = H(ld.ToString("yyyy-MM-dd HH:mm") + " UTC");
                    sb.Append($@"        <tr>
          <td><a href=""{H(ExplorerAddressUrl(chainAbbrev, seller))}"" target=""_blank"" rel=""noopener"">{H(seller)}</a></td>
          <td class=""amount"">{qty}</td>
          <td class=""amount"">{value:F2}</td>
          <td>{listedHtml}</td>
        </tr>
");
                }
                sb.Append(@"      </tbody>
    </table>
  </div>
");
            }

            // ── Raw JSON link ─────────────────────────────────────────────────
            sb.Append($@"  <div style=""text-align:center; padding: 10px 0 20px; font-size:0.82em; color:#555;"">
    <a href=""/root/{H(address)}/OBJ.json"">View raw OBJ.json</a>
  </div>
");

            // ── JavaScript for lazy IPFS media loading ────────────────────────
            if (hasArtifact || hasThumbnail || !string.IsNullOrEmpty(imageGatewayUrl))
            {
                string thumbUrl   = H(hasThumbnail ? imageGatewayUrl : (!string.IsNullOrEmpty(imageGatewayUrl) ? imageGatewayUrl : ""));
                string artifactUrl = H(urnGatewayUrl);
                string artifactFn  = H(urnFilename);
                bool isImage = ImageExtensions.Contains(urnExt);
                bool isVideo = VideoExtensions.Contains(urnExt);
                bool isAudio = AudioExtensions.Contains(urnExt);

                sb.Append($@"<script>
(function() {{
  // Load thumbnail
  var thumbUrl = '{thumbUrl}';
  if (thumbUrl) {{
    var thumbEl = document.getElementById('obj-thumb');
    if (thumbEl) thumbEl.src = thumbUrl;
  }}

  // Load artifact asynchronously (deferred via event loop)
  var artifactUrl = '{artifactUrl}';
  var artifactFn  = '{artifactFn}';
  var card = document.getElementById('artifact-card');
  if (!card || !artifactUrl) return;

  setTimeout(function() {{
    var ext = artifactFn ? artifactFn.split('.').pop().toLowerCase() : '';
    var el = '';
    var imgExts = ['jpg','jpeg','png','gif','webp','bmp','svg'];
    var vidExts = ['mp4','m4v','webm','ogv','mov'];
    var audExts = ['mp3','wav','ogg','flac','aac','m4a'];
    if (imgExts.indexOf(ext) >= 0) {{
      var img = document.createElement('img');
      img.src = artifactUrl;
      img.alt = artifactFn;
      img.style.cssText = 'width:100%;display:block;max-height:400px;object-fit:contain;background:#000;';
      img.onerror = function() {{ this.parentElement.innerHTML = '<div class=""media-caption"">Failed to load image.</div>'; }};
      var link = document.createElement('a');
      link.href = artifactUrl; link.target = '_blank'; link.rel = 'noopener';
      link.appendChild(img);
      card.innerHTML = '';
      card.appendChild(link);
    }} else if (vidExts.indexOf(ext) >= 0) {{
      var vid = document.createElement('video');
      vid.controls = true; vid.src = artifactUrl;
      vid.style.cssText = 'width:100%;display:block;';
      vid.onerror = function() {{ this.parentElement.innerHTML = '<div class=""media-caption"">Failed to load video.</div>'; }};
      card.innerHTML = '';
      card.appendChild(vid);
    }} else if (audExts.indexOf(ext) >= 0) {{
      var aud = document.createElement('audio');
      aud.controls = true; aud.src = artifactUrl;
      aud.style.cssText = 'width:100%;display:block;';
      aud.onerror = function() {{ this.parentElement.innerHTML = '<div class=""media-caption"">Failed to load audio.</div>'; }};
      card.innerHTML = '';
      card.appendChild(aud);
    }} else {{
      var img2 = document.createElement('img');
      img2.src = artifactUrl;
      img2.alt = artifactFn;
      img2.style.cssText = 'width:100%;display:block;max-height:400px;object-fit:contain;background:#000;';
      img2.onerror = function() {{ this.style.display = 'none'; }};
      var link2 = document.createElement('a');
      link2.href = artifactUrl; link2.target = '_blank'; link2.rel = 'noopener';
      link2.appendChild(img2);
      card.innerHTML = '';
      card.appendChild(link2);
    }}
    var cap = document.createElement('div');
    cap.className = 'media-caption';
    var capLink = document.createElement('a');
    capLink.href = artifactUrl; capLink.target = '_blank'; capLink.rel = 'noopener';
    capLink.textContent = artifactFn || 'Open artifact';
    cap.appendChild(capLink);
    card.appendChild(cap);
  }}, 0);
}})();
</script>
");
            }

            sb.Append("</div>\n</body>\n</html>");
            return sb.ToString();
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
  <meta property=""og:description"" content=""On-chain root record etched {H(blockDateDisplay)}"">
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
            AddMeta(sb, "Transaction ID", $@"<a href=""{H(ExplorerTxUrl(chainAbbrev, txid))}"" target=""_blank"" rel=""noopener"">{H(txid)}</a>");
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
          <td><a href=""{H(ExplorerAddressUrl(chainAbbrev, addr))}"" target=""_blank"" rel=""noopener"">{H(addr)}</a></td>
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
                    AddMeta(sb, "Signed By", $@"<a href=""{H(ExplorerAddressUrl(chainAbbrev, signedBy))}"" target=""_blank"" rel=""noopener"">{H(signedBy)}</a>");
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

        private static string ExplorerTxUrl(string chain, string txid) => chain switch
        {
            "TBTC" => $"https://mempool.space/testnet/tx/{txid}",
            "LTC"  => $"https://litecoinspace.org/tx/{txid}",
            "DOG"  => $"https://blockchair.com/dogecoin/transaction/{txid}",
            "MZC"  => $"https://mazacha.in/tx/{txid}",
            _      => $"https://mempool.space/tx/{txid}",
        };

        private static string ExplorerAddressUrl(string chain, string address) => chain switch
        {
            "TBTC" => $"https://mempool.space/testnet/address/{address}",
            "LTC"  => $"https://litecoinspace.org/address/{address}",
            "DOG"  => $"https://blockchair.com/dogecoin/address/{address}",
            "MZC"  => $"https://mazacha.in/address/{address}",
            _      => $"https://mempool.space/address/{address}",
        };
    }
}
