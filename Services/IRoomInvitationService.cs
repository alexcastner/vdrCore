using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using twoSaaSCore.Models;

namespace twoSaaSCore.Services
{
    public interface IRoomInvitationService
    {
        Task<RoomInvitation> CreateInvitationAsync(Guid tenantId, Guid roomId, string email, RoomRole role, string invitedByUserId, string? message = null, int expiresInDays = 7, CancellationToken ct = default);
        Task<(bool Success, Guid? RoomId, string? Error)> AcceptInvitationAsync(string token, string userId, string userEmail, CancellationToken ct = default);
        Task RevokeInvitationAsync(Guid tenantId, Guid roomId, int invitationId, CancellationToken ct = default);
        Task<List<RoomInvitation>> ListPendingInvitationsAsync(Guid tenantId, Guid roomId, CancellationToken ct = default);
        Task<RoomInvitation?> GetInvitationByTokenAsync(string token, CancellationToken ct = default);
    }
}
