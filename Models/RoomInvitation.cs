using System;
using twoSaaSCore.Services;

namespace twoSaaSCore.Models
{
    public class RoomInvitation : ITenantEntity
    {
        public int Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid RoomId { get; set; }
        public string Email { get; set; } = string.Empty;
        public RoomRole Role { get; set; }
        public string Token { get; set; } = string.Empty;
        public InvitationStatus Status { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }
        public string? InvitedByUserId { get; set; }
        public string? AcceptedByUserId { get; set; }
        public DateTimeOffset? AcceptedUtc { get; set; }
        public string? Message { get; set; }
    }
}
