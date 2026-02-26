using System;

namespace twoSaaSCore.Models
{
    public class RoomAnswer : ITenantEntity
    {
        public int Id { get; set; }
        public Guid TenantId { get; set; }
        public int QuestionId { get; set; }
        public string Body { get; set; } = string.Empty;
        public string AnsweredByUserId { get; set; } = string.Empty;
        public string? AnsweredByEmail { get; set; }
        public DateTimeOffset AnsweredUtc { get; set; }

        public RoomQuestion? Question { get; set; }
    }
}
