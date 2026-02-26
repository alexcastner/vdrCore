using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using twoSaaSCore.Models;

namespace twoSaaSCore.Services
{
    public interface IRoomPermissionService
    {
        Task<RoomPermission> GetEffectivePermissionsAsync(Guid tenantId, Guid roomId, string userId, string? folderPath = null, CancellationToken ct = default);
        Task<bool> HasPermissionAsync(Guid tenantId, Guid roomId, string userId, RoomPermission required, string? folderPath = null, CancellationToken ct = default);
        Task GrantAccessAsync(Guid tenantId, Guid roomId, string userId, RoomRole role, string? grantedBy, string? folderPath = null, RoomPermission? overrides = null, CancellationToken ct = default);
        Task RevokeAccessAsync(Guid tenantId, Guid roomId, string userId, string? folderPath = null, CancellationToken ct = default);
        Task UpdateAccessAsync(Guid tenantId, Guid roomId, string userId, RoomRole role, string? folderPath = null, RoomPermission? overrides = null, CancellationToken ct = default);
        Task<List<RoomMember>> ListMembersAsync(Guid tenantId, Guid roomId, CancellationToken ct = default);
        Task<List<Guid>> ListAccessibleRoomIdsAsync(Guid tenantId, string userId, CancellationToken ct = default);
        RoomPermission GetRolePermissions(RoomRole role);
    }
}
