using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerUI;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<P2FK.IO.Wrapper>();
builder.Services.AddHttpClient("bitfossil", client =>
{
    client.BaseAddress = new Uri("https://bitfossil.org/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", "p2fk.io/1.0");
});

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

app.UseSwagger();
app.UseStaticFiles();
app.UseRequestTimeouts();
app.UseSwaggerUI(options =>
    {
        //removes the /swagger/ from the path
        options.RoutePrefix = string.Empty;

        //update to incude your own api and version
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "P2FK.IO V1");

        //update to incude your own api 
        options.DocumentTitle = "P2FK.IO";

        options.DisplayRequestDuration();

        //update to use your own images and favicons
        options.HeadContent = @"
        <link rel=""apple-touch-icon"" sizes=""180x180"" href=""./apple-touch-icon.png"" />
        <link rel=""icon"" type=""image/png"" sizes=""32x32"" href=""./favicon-32x32.png"" />
        <link rel=""icon"" type=""image/png"" sizes=""16x16"" href=""./favicon-16x16.png"" />
        <style>
            .swagger-ui img  {
                content: url('./HugPuddle.jpg');
                width: 50px;
                height: auto;
            }
        </style>";


        //added because large json output styling slows down the swagger ui
        options.ConfigObject.AdditionalItems["syntaxHighlight"] = new Dictionary<string, object>
         {
             ["activated"] = false
         };


    }
    );

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
