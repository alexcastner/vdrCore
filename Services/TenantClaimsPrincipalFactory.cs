using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using twoSaaSCore.Constants;
using twoSaaSCore.Models;

namespace twoSaaSCore.Services
{
    public class TenantClaimsPrincipalFactory : UserClaimsPrincipalFactory<ApplicationUser>
    {
        public TenantClaimsPrincipalFactory(
            UserManager<ApplicationUser> userManager,
            IOptions<IdentityOptions> optionsAccessor)
            : base(userManager, optionsAccessor)
        {
        }

        protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
        {
            var identity = await base.GenerateClaimsAsync(user);

            if (user.TenantId != Guid.Empty &&
                !identity.HasClaim(c => c.Type == TenantConstants.TenantIdClaimType))
            {
                identity.AddClaim(new Claim(TenantConstants.TenantIdClaimType, user.TenantId.ToString()));
            }

            if (!string.IsNullOrWhiteSpace(user.Subdomain) &&
                !identity.HasClaim(c => c.Type == "subdomain"))
            {
                identity.AddClaim(new Claim("subdomain", user.Subdomain));
            }

            return identity;
        }
    }
}