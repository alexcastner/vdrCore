using System;

namespace twoSaaSCore.Models
{
    public class NdaAcceptance : ITenantEntity
    {
        public int Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid RoomId { get; set; }
        public string UserId { get; set; } = string.Empty;
        public DateTimeOffset AcceptedUtc { get; set; }
        public string? IpAddress { get; set; }
    }
}
