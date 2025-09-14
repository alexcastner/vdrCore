using System;

namespace twoSaaSCore.Models
{
    public class Tenant
    {
        public Guid TenantId { get; set; }
        public string TenantName { get; set; } = string.Empty;
        public string Subdomain { get; set; } = string.Empty;
    }
}
