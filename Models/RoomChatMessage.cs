using System;
using System.ComponentModel.DataAnnotations;

namespace twoSaaSCore.Models
{
    /// <summary>Stores chat history messages for a room thread.</summary>
    public class RoomChatMessage : ITenantEntity
    {
        public int Id { get; set; }

        public Guid TenantId { get; set; }

        public Guid RoomId { get; set; }

        [MaxLength(64)]
        public string ThreadId { get; set; } = string.Empty;

        [MaxLength(450)]
        public string UserId { get; set; } = string.Empty;

        [MaxLength(16)]
        public string Role { get; set; } = "user";

        public string Content { get; set; } = string.Empty;

        public DateTimeOffset CreatedUtc { get; set; }
    }
}
