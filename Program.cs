using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerUI;
using System.Runtime.Versioning;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "P2FK.IO API",
        Version = "v1",
        Description =
            "The **Sup!? / P2FK** protocol API — search for on-chain messages, user profiles, " +
            "and digital objects (NFTs) inscribed across Bitcoin (mainnet & testnet), Litecoin, Dogecoin, and Mazacoin.\n\n" +
            "**Chain selection:**  \n" +
            "- `mainnet=true` (default) + `blockchain=BTC` → Bitcoin mainnet  \n" +
            "- `mainnet=false` + `blockchain=BTC` → Bitcoin testnet  \n" +
            "- `blockchain=LTC | DOG | MZC` → Litecoin / Dogecoin / Mazacoin\n\n" +
            "All endpoints are read-only.",
    });

    // Include XML doc comments generated from the triple-slash summaries on every controller
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath)) c.IncludeXmlComments(xmlPath);

    // Group endpoints by logical category instead of one-tag-per-controller
    c.TagActionsBy(api =>
    {
        var ctrl = api.ActionDescriptor.RouteValues["controller"] ?? "";
        if (ctrl.Contains("KnownRoots") || ctrl.Contains("KnownObjects") || ctrl.Contains("KnownProfiles"))
            return ["Search"];
        if (ctrl.Contains("PublicMessages") || ctrl.Contains("PrivateMessages") ||
            ctrl.Contains("Root") || ctrl.Contains("Roots"))
            return ["Messages & Roots"];
        if (ctrl.Contains("Profile"))
            return ["Profiles"];
        if (ctrl.Contains("Object"))
            return ["Objects"];
        if (ctrl.Contains("Inquiry") || ctrl.Contains("Inquiries"))
            return ["Inquiries"];
        if (ctrl.Contains("Keyword") || ctrl.Contains("PublicAddress"))
            return ["Keywords"];
        return [ctrl];
    });
    c.OrderActionsBy(a => a.ActionDescriptor.RouteValues["controller"]);
});
builder.Services.AddSingleton<P2FK.IO.Wrapper>();
builder.Services.AddMemoryCache(options =>
{
    // Cap the total number of distinct cache entries at 1024.
    // Each entry is registered with size=1.  Combined with the 5-minute TTL this
    // prevents unbounded growth from cache-key explosion or runaway user queries.
    // When the limit is reached the oldest 25 % of entries are evicted.
    options.SizeLimit = 1024;
    options.CompactionPercentage = 0.25;
});
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<P2FK.IO.Services.WindowsSearchService>();
    builder.Services.AddHostedService<P2FK.IO.Services.CacheWarmingService>();
}

// Allow requests to take up to MaxTimeoutSeconds before the server cancels them
builder.Services.AddRequestTimeouts(options =>
    options.DefaultPolicy = new RequestTimeoutPolicy
    {
        Timeout = TimeSpan.FromSeconds(P2FK.IO.Wrapper.MaxTimeoutSeconds)
    });

// Keep the Kestrel keep-alive and header timeouts well above the max query time
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(P2FK.IO.Wrapper.MaxTimeoutSeconds + 10);
    kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(P2FK.IO.Wrapper.MaxTimeoutSeconds + 10);
});

var app = builder.Build();

// Serve on-chain files from the root folder at /root/{txid}/{filename}
var wrapper = app.Services.GetRequiredService<P2FK.IO.Wrapper>();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(wrapper.RootPath),
    RequestPath = "/root",
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream"
});

app.UseSwagger();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRequestTimeouts();
app.UseSwaggerUI(options =>
    {
        //swagger docs are served at /API
        options.RoutePrefix = "API";

        //update to incude your own api and version
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "P2FK.IO V1");

        //update to incude your own api 
        options.DocumentTitle = "P2FK.IO";

        options.DisplayRequestDuration();

        //update to use your own images and favicons
        options.HeadContent = @"
        <link rel=""apple-touch-icon"" sizes=""180x180"" href=""/apple-touch-icon.png"" />
        <link rel=""icon"" type=""image/png"" sizes=""32x32"" href=""/favicon-32x32.png"" />
        <link rel=""icon"" type=""image/png"" sizes=""16x16"" href=""/favicon-16x16.png"" />
        <style>
            .swagger-ui img  {
                content: url('/HugPuddle.jpg');
                width: 50px;
                height: auto;
            }
        </style>";


        //added because large json output styling slows down the swagger ui
        options.ConfigObject.AdditionalItems["syntaxHighlight"] = new Dictionary<string, object>
         {
             ["activated"] = false
         };

        // Inject the P2FK dark theme stylesheet
        options.InjectStylesheet("/swagger-dark.css");

        // Inject the copyright footer
        options.InjectJavascript("/swagger-footer.js");


    }
    );

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
