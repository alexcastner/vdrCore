using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using twoSaaSCore.Constants;

namespace twoSaaSCore.Services
{
    public class HttpContextTenantProvider : ITenantProvider
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public HttpContextTenantProvider(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public Guid GetTenantId()
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext?.User?.Identity?.IsAuthenticated == true)
            {
                var claim = httpContext.User.FindFirst(TenantConstants.TenantIdClaimType);
                if (claim != null && Guid.TryParse(claim.Value, out var id))
                {
                    return id;
                }
            }
            return Guid.Empty;
        }

        public string? GetSubdomain()
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext == null) return null;
            if (httpContext.Items.TryGetValue(TenantConstants.HttpContextItemSubdomain, out var value) && value is string s)
            {
                return s;
            }
            return null;
        }
    }
}
