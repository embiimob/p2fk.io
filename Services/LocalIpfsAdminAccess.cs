using System.Net;

namespace P2FK.IO.Services
{
    public static class LocalIpfsAdminAccess
    {
        public const string SwaggerPathPrefix = "/ipfs/cache-admin";
        public const string SwaggerHost = "127.0.0.1";

        public static bool IsLoopbackRequest(HttpContext httpContext) =>
            httpContext.Connection.RemoteIpAddress is IPAddress remoteIp
            && IPAddress.IsLoopback(remoteIp);

        public static bool ShouldExposeInSwagger(HttpContext httpContext) =>
            IsLoopbackRequest(httpContext)
            && string.Equals(httpContext.Request.Host.Host, SwaggerHost, StringComparison.OrdinalIgnoreCase);
    }
}
