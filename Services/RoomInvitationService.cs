using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using twoSaaSCore.Data;
using twoSaaSCore.Models;

namespace twoSaaSCore.Services
{
    public class RoomInvitationService : IRoomInvitationService
    {
        private readonly ApplicationDbContext _db;
        private readonly IRoomPermissionService _permissions;

        public RoomInvitationService(ApplicationDbContext db, IRoomPermissionService permissions)
        {
            _db = db;
            _permissions = permissions;
        }

        public async Task<RoomInvitation> CreateInvitationAsync(Guid tenantId, Guid roomId, string email, RoomRole role, string invitedByUserId, string? message = null, int expiresInDays = 7, CancellationToken ct = default)
        {
            email = email.Trim().ToLowerInvariant();

            // Check for existing pending invitation for same email+room
            var existing = await _db.RoomInvitations
                .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.RoomId == roomId
                    && i.Email == email && i.Status == InvitationStatus.Pending
                    && i.ExpiresUtc > DateTimeOffset.UtcNow, ct);

            if (existing != null)
            {
                // Update the existing invitation
                existing.Role = role;
                existing.Message = message;
                existing.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(expiresInDays);
                existing.InvitedByUserId = invitedByUserId;
                existing.CreatedUtc = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                return existing;
            }

            var invitation = new RoomInvitation
            {
                TenantId = tenantId,
                RoomId = roomId,
                Email = email,
                Role = role,
                Token = GenerateToken(),
                Status = InvitationStatus.Pending,
                CreatedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(expiresInDays),
                InvitedByUserId = invitedByUserId,
                Message = message
            };

            _db.RoomInvitations.Add(invitation);
            await _db.SaveChangesAsync(ct);
            return invitation;
        }

        public async Task<(bool Success, Guid? RoomId, string? Error)> AcceptInvitationAsync(string token, string userId, string userEmail, CancellationToken ct = default)
        {
            // IgnoreQueryFilters so invitation is found regardless of tenant context
            var invitation = await _db.RoomInvitations
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.Token == token, ct);

            if (invitation == null)
                return (false, null, "Invitation not found.");

            if (invitation.Status == InvitationStatus.Accepted)
                return (false, invitation.RoomId, "This invitation has already been used.");

            if (invitation.Status == InvitationStatus.Revoked)
                return (false, null, "This invitation has been revoked.");

            if (invitation.ExpiresUtc < DateTimeOffset.UtcNow)
            {
                invitation.Status = InvitationStatus.Expired;
                await _db.SaveChangesAsync(ct);
                return (false, null, "This invitation has expired.");
            }

            if (invitation.Status != InvitationStatus.Pending)
                return (false, null, "This invitation is no longer valid.");

            // Grant room access
            await _permissions.GrantAccessAsync(
                invitation.TenantId,
                invitation.RoomId,
                userId,
                invitation.Role,
                invitation.InvitedByUserId,
                ct: ct);

            // Mark invitation as accepted
            invitation.Status = InvitationStatus.Accepted;
            invitation.AcceptedByUserId = userId;
            invitation.AcceptedUtc = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            return (true, invitation.RoomId, null);
        }

        public async Task RevokeInvitationAsync(Guid tenantId, Guid roomId, int invitationId, CancellationToken ct = default)
        {
            var invitation = await _db.RoomInvitations
                .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.RoomId == roomId && i.Id == invitationId && i.Status == InvitationStatus.Pending, ct);

            if (invitation != null)
            {
                invitation.Status = InvitationStatus.Revoked;
                await _db.SaveChangesAsync(ct);
            }
        }

        public async Task<List<RoomInvitation>> ListPendingInvitationsAsync(Guid tenantId, Guid roomId, CancellationToken ct = default)
        {
            return await _db.RoomInvitations
                .Where(i => i.TenantId == tenantId && i.RoomId == roomId && i.Status == InvitationStatus.Pending && i.ExpiresUtc > DateTimeOffset.UtcNow)
                .OrderByDescending(i => i.CreatedUtc)
                .ToListAsync(ct);
        }

        public async Task<RoomInvitation?> GetInvitationByTokenAsync(string token, CancellationToken ct = default)
        {
            return await _db.RoomInvitations
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.Token == token, ct);
        }

        private static string GenerateToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=');
        }
    }
}
