using System;

namespace twoSaaSCore.Services
{
    public interface ITenantProvider
    {
        Guid GetTenantId();
        string? GetSubdomain();
    }
}
