using System;
using System.ComponentModel.DataAnnotations;

namespace twoSaaSCore.Models
{
    /// <summary>Stores per-user chat thread metadata for a room.</summary>
    public class RoomChatThread : ITenantEntity
    {
        public int Id { get; set; }

        public Guid TenantId { get; set; }

        public Guid RoomId { get; set; }

        [MaxLength(64)]
        public string ThreadId { get; set; } = string.Empty;

        [MaxLength(450)]
        public string UserId { get; set; } = string.Empty;

        [MaxLength(200)]
        public string? Title { get; set; }

        public bool IsSaved { get; set; }

        public int MessageCount { get; set; }

        public DateTimeOffset CreatedUtc { get; set; }

        public DateTimeOffset LastActivityUtc { get; set; }
    }
}
