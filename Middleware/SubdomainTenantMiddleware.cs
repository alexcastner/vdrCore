using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using twoSaaSCore.Constants;

namespace twoSaaSCore.Middleware
{
    public class SubdomainTenantMiddleware
    {
        private readonly RequestDelegate _next;

        public SubdomainTenantMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var host = context.Request.Host.Host;
            var subdomain = ExtractSubdomain(host);
            if (!string.IsNullOrWhiteSpace(subdomain))
            {
                context.Items[TenantConstants.HttpContextItemSubdomain] = subdomain;
            }
            await _next(context);
        }

        private static string? ExtractSubdomain(string host)
        {
            // supports localhost and multi-level subdomains
            // e.g., foo.example.com -> foo, foo.localhost -> foo
            if (string.IsNullOrWhiteSpace(host)) return null;

            var parts = host.Split('.');
            if (parts.Length <= 1)
            {
                return null; // no subdomain
            }

            // handle localhost with port where host may be "localhost"
            if (parts.Length == 1 || host.Equals("localhost", System.StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return parts[0];
        }
    }
}
