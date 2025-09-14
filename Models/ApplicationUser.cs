using System;
using Microsoft.AspNetCore.Identity;

namespace twoSaaSCore.Models
{
    public class ApplicationUser : IdentityUser
    {
        public Guid TenantId { get; set; }
        public string Subdomain { get; set; } = string.Empty;
    }
}
