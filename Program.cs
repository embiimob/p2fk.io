using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerUI;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<P2FK.IO.Wrapper>();

var app = builder.Build();

app.UseSwagger();
app.UseStaticFiles();
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
            #api-examples-nav {
                background: #1e1e1e;
                padding: 10px 20px;
                display: flex;
                align-items: center;
                gap: 16px;
                border-bottom: 1px solid #333;
                font-family: sans-serif;
            }
            #api-examples-nav span {
                color: #bb86fc;
                font-weight: bold;
                font-size: 14px;
                white-space: nowrap;
            }
            #api-examples-nav a {
                color: #bb86fc;
                text-decoration: none;
                font-size: 14px;
                padding: 6px 14px;
                border: 1px solid #bb86fc;
                border-radius: 4px;
                transition: background 0.2s, color 0.2s;
            }
            #api-examples-nav a:hover {
                background: #bb86fc;
                color: #1e1e1e;
            }
        </style>
        <script>
            document.addEventListener('DOMContentLoaded', function() {
                var nav = document.createElement('div');
                nav.id = 'api-examples-nav';
                nav.innerHTML = '<span>API Examples:</span>'
                    + '<a href=""/socials.html"" target=""_blank"">Socials</a>'
                    + '<a href=""/objects.html"" target=""_blank"">Objects</a>'
                    + '<a href=""/lotto.html"" target=""_blank"">Lotto</a>'
                    + '<a href=""/world.html"" target=""_blank"">World</a>';
                document.body.insertBefore(nav, document.body.firstChild);
            });
        </script>";


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
