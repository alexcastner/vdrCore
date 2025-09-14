using System;

namespace twoSaaSCore.Models
{
    public interface ITenantEntity
    {
        Guid TenantId { get; set; }
    }
}
