using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using twoSaaSCore.Data;
using twoSaaSCore.Models;

namespace twoSaaSCore.Services
{
    public class RoomPermissionService : IRoomPermissionService
    {
        private readonly ApplicationDbContext _db;

        public RoomPermissionService(ApplicationDbContext db)
        {
            _db = db;
        }

        public RoomPermission GetRolePermissions(RoomRole role) => role switch
        {
            RoomRole.Viewer     => RoomPermission.AccessRoom | RoomPermission.ViewDocuments,
            RoomRole.Downloader => RoomPermission.AccessRoom | RoomPermission.ViewDocuments | RoomPermission.Download | RoomPermission.Print,
            RoomRole.Editor     => RoomPermission.AccessRoom | RoomPermission.ViewDocuments | RoomPermission.Download | RoomPermission.Print
                                 | RoomPermission.Upload | RoomPermission.ManageFolders,
            RoomRole.Admin      => RoomPermission.AccessRoom | RoomPermission.ViewDocuments | RoomPermission.Download | RoomPermission.Print
                                 | RoomPermission.Upload | RoomPermission.ManageFolders
                                 | RoomPermission.DeleteFiles | RoomPermission.ManagePermissions | RoomPermission.ViewAuditLog,
            RoomRole.Owner      => RoomPermission.AccessRoom | RoomPermission.ViewDocuments | RoomPermission.Download | RoomPermission.Print
                                 | RoomPermission.Upload | RoomPermission.ManageFolders
                                 | RoomPermission.DeleteFiles | RoomPermission.ManagePermissions | RoomPermission.ViewAuditLog
                                 | RoomPermission.ManageRoom,
            _ => RoomPermission.None
        };

        public async Task<RoomPermission> GetEffectivePermissionsAsync(Guid tenantId, Guid roomId, string userId, string? folderPath = null, CancellationToken ct = default)
        {
            var memberships = await _db.RoomMembers
                .Where(m => m.TenantId == tenantId && m.RoomId == roomId && m.UserId == userId)
                .ToListAsync(ct);

            if (memberships.Count == 0)
                return RoomPermission.None;

            // Room-wide membership (FolderPath is null or empty)
            var roomWide = memberships.FirstOrDefault(m => string.IsNullOrEmpty(m.FolderPath));
            var basePerms = roomWide != null
                ? (roomWide.PermissionOverrides ?? GetRolePermissions(roomWide.Role))
                : RoomPermission.None;

            if (string.IsNullOrEmpty(folderPath) || roomWide == null)
                return basePerms;

            // Find the most-specific folder-scoped override
            var normalized = NormalizeFolderPath(folderPath);
            RoomMember? bestMatch = null;
            var bestLen = 0;

            foreach (var m in memberships.Where(m => !string.IsNullOrEmpty(m.FolderPath)))
            {
                var mPath = NormalizeFolderPath(m.FolderPath);
                if (normalized.StartsWith(mPath, StringComparison.OrdinalIgnoreCase) && mPath.Length > bestLen)
                {
                    bestMatch = m;
                    bestLen = mPath.Length;
                }
            }

            if (bestMatch != null)
                return bestMatch.PermissionOverrides ?? GetRolePermissions(bestMatch.Role);

            return basePerms;
        }

        public async Task<bool> HasPermissionAsync(Guid tenantId, Guid roomId, string userId, RoomPermission required, string? folderPath = null, CancellationToken ct = default)
        {
            var effective = await GetEffectivePermissionsAsync(tenantId, roomId, userId, folderPath, ct);
            return (effective & required) == required;
        }

        public async Task GrantAccessAsync(Guid tenantId, Guid roomId, string userId, RoomRole role, string? grantedBy, string? folderPath = null, RoomPermission? overrides = null, CancellationToken ct = default)
        {
            var normalizedPath = NormalizeFolderPath(folderPath);
            var existing = await _db.RoomMembers
                .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.RoomId == roomId && m.UserId == userId
                    && (string.IsNullOrEmpty(normalizedPath) ? string.IsNullOrEmpty(m.FolderPath) : m.FolderPath == normalizedPath), ct);

            if (existing != null)
            {
                existing.Role = role;
                existing.PermissionOverrides = overrides;
                existing.GrantedUtc = DateTimeOffset.UtcNow;
                existing.GrantedByUserId = grantedBy;
            }
            else
            {
                _db.RoomMembers.Add(new RoomMember
                {
                    TenantId = tenantId,
                    RoomId = roomId,
                    UserId = userId,
                    Role = role,
                    PermissionOverrides = overrides,
                    FolderPath = string.IsNullOrEmpty(normalizedPath) ? null : normalizedPath,
                    GrantedUtc = DateTimeOffset.UtcNow,
                    GrantedByUserId = grantedBy
                });
            }
            await _db.SaveChangesAsync(ct);
        }

        public async Task RevokeAccessAsync(Guid tenantId, Guid roomId, string userId, string? folderPath = null, CancellationToken ct = default)
        {
            var normalizedPath = NormalizeFolderPath(folderPath);
            var existing = await _db.RoomMembers
                .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.RoomId == roomId && m.UserId == userId
                    && (string.IsNullOrEmpty(normalizedPath) ? string.IsNullOrEmpty(m.FolderPath) : m.FolderPath == normalizedPath), ct);

            if (existing != null)
            {
                _db.RoomMembers.Remove(existing);
                await _db.SaveChangesAsync(ct);
            }
        }

        public async Task UpdateAccessAsync(Guid tenantId, Guid roomId, string userId, RoomRole role, string? folderPath = null, RoomPermission? overrides = null, CancellationToken ct = default)
        {
            var normalizedPath = NormalizeFolderPath(folderPath);
            var existing = await _db.RoomMembers
                .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.RoomId == roomId && m.UserId == userId
                    && (string.IsNullOrEmpty(normalizedPath) ? string.IsNullOrEmpty(m.FolderPath) : m.FolderPath == normalizedPath), ct);

            if (existing != null)
            {
                existing.Role = role;
                existing.PermissionOverrides = overrides;
                await _db.SaveChangesAsync(ct);
            }
        }

        public async Task<List<RoomMember>> ListMembersAsync(Guid tenantId, Guid roomId, CancellationToken ct = default)
        {
            return await _db.RoomMembers
                .Where(m => m.TenantId == tenantId && m.RoomId == roomId)
                .OrderBy(m => m.UserId)
                .ToListAsync(ct);
        }

        public async Task<List<Guid>> ListAccessibleRoomIdsAsync(Guid tenantId, string userId, CancellationToken ct = default)
        {
            return await _db.RoomMembers
                .Where(m => m.TenantId == tenantId && m.UserId == userId)
                .Select(m => m.RoomId)
                .Distinct()
                .ToListAsync(ct);
        }

        private static string NormalizeFolderPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return string.Empty;
            return string.Join('/', segments) + "/";
        }
    }
}
