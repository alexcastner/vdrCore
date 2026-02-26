using System;
using twoSaaSCore.Services;

namespace twoSaaSCore.Models
{
    public class RoomMember : ITenantEntity
    {
        public int Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid RoomId { get; set; }
        public string UserId { get; set; } = string.Empty;
        public RoomRole Role { get; set; }
        public RoomPermission? PermissionOverrides { get; set; }
        public string? FolderPath { get; set; }
        public DateTimeOffset GrantedUtc { get; set; }
        public string? GrantedByUserId { get; set; }
    }
}
