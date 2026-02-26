using System;

namespace twoSaaSCore.Models
{
    public class RoomQuestion : ITenantEntity
    {
        public int Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid RoomId { get; set; }
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string AskedByUserId { get; set; } = string.Empty;
        public string? AskedByEmail { get; set; }
        public DateTimeOffset AskedUtc { get; set; }
        public QuestionStatus Status { get; set; }
        public string? AssignedToUserId { get; set; }
    }
}
