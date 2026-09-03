using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.IIS;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi.Models;
using P2FK.IO.HealthChecks;
using P2FK.IO.Options;
using P2FK.IO.Services;
using Serilog;
using Swashbuckle.AspNetCore.SwaggerUI;
using System.Text.Json;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var ingressOptions = builder.Configuration.GetSection(IpfsIngressOptions.SectionName).Get<IpfsIngressOptions>() ?? new IpfsIngressOptions();

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

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
            "Includes temporary IPFS ingress relay endpoints for one-hour Kubo pinning.",
    });

    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath)) c.IncludeXmlComments(xmlPath);
    c.DocumentFilter<AllModelSchemasDocumentFilter>();

    c.TagActionsBy(api =>
    {
        var ctrl = api.ActionDescriptor.RouteValues["controller"] ?? "";
        if (ctrl.Contains("KnownRoots") || ctrl.Contains("KnownObjects") || ctrl.Contains("KnownProfiles") || ctrl.Contains("TrendingRootSearch"))
            return ["Search"];
        if (ctrl.Contains("CacheStatus"))
            return ["Cache"];
        if (ctrl.Contains("Ipfs"))
            return ["IPFS Ingress"];
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
builder.Services.AddSingleton<P2FK.IO.Services.CacheStatusService>();
builder.Services.AddSingleton<P2FK.IO.Services.RootSearchTrendService>();
builder.Services.AddSingleton<IngressMetadataStore>();
builder.Services.AddSingleton<IKuboIngressService, KuboIngressService>();
builder.Services.AddSingleton<IpfsIngressService>();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient(nameof(KuboIngressService));
builder.Services.Configure<IpfsIngressOptions>(builder.Configuration.GetSection(IpfsIngressOptions.SectionName));
builder.Services.AddHealthChecks().AddCheck<IpfsIngressHealthCheck>("ipfs_ingress");
builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = 1024;
    options.CompactionPercentage = 0.25;
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, _) =>
    {
        if (context.HttpContext.Response.HasStarted)
            return;

        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsync("{\"error\":\"Upload rate limit exceeded\"}");
    };

    options.AddPolicy("IpfsUpload", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = ingressOptions.UploadRequestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<P2FK.IO.Services.WindowsSearchService>();
    builder.Services.AddHostedService<P2FK.IO.Services.CacheWarmingService>();
    builder.Services.AddHostedService<P2FK.IO.Services.LiveMempoolMonitorService>();
}

builder.Services.AddHostedService<ManagedKuboService>();
builder.Services.AddHostedService<IngressExpirationWorker>();
builder.Services.AddHostedService<IpfsCacheTransferWorker>();
builder.Services.AddRequestTimeouts(options =>
    options.DefaultPolicy = new RequestTimeoutPolicy
    {
        Timeout = TimeSpan.FromSeconds(P2FK.IO.Wrapper.MaxTimeoutSeconds)
    });
builder.Services.Configure<IISServerOptions>(options => options.MaxRequestBodySize = null);
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = ingressOptions.MaxUploadBytes;
});

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(P2FK.IO.Wrapper.MaxTimeoutSeconds + 10);
    kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(P2FK.IO.Wrapper.MaxTimeoutSeconds + 10);
    kestrel.Limits.MaxRequestBodySize = null;
});

var app = builder.Build();

await app.Services.GetRequiredService<IngressMetadataStore>().InitializeAsync();

var wrapper = app.Services.GetRequiredService<P2FK.IO.Wrapper>();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(wrapper.RootPath),
    RequestPath = "/root",
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream"
});

app.UseSerilogRequestLogging();
app.UseSwagger();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRequestTimeouts();
app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "API";
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "P2FK.IO V1");
    options.DocumentTitle = "P2FK.IO";
    options.DisplayRequestDuration();
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

    options.ConfigObject.AdditionalItems["syntaxHighlight"] = new Dictionary<string, object>
    {
        ["activated"] = false
    };

    options.InjectStylesheet("/swagger-dark.css");
    options.InjectJavascript("/swagger-footer.js");
});

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health/ipfs", new HealthCheckOptions
{
    Predicate = registration => registration.Name == "ipfs_ingress",
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        bool kuboConnected = report.Entries.TryGetValue("ipfs_ingress", out HealthReportEntry entry)
            && entry.Data.TryGetValue("kuboConnected", out object? value)
            && value is bool connected
            && connected;

        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            healthy = report.Status == HealthStatus.Healthy,
            kuboConnected
        }));
    }
});

app.MapControllers();

app.Run();
