using System;

namespace twoSaaSCore.Models
{
    public class DocumentAuditLog
    {
        public long AuditLogId { get; set; }
        public DateTime ActionUtc { get; set; }
        public Guid TenantId { get; set; }
        public Guid? RoomId { get; set; }
        public Guid? FileId { get; set; }
        public string? UserId { get; set; }
        public string Action { get; set; } = default!;
        public string? FileName { get; set; }
        public long? FileSize { get; set; }
        public string? FileSha256 { get; set; }
        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }
        public Guid CorrelationId { get; set; }
        public string? ExtraJson { get; set; }
        public string? SrcExt { get; set; }
    }
}