using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace twoSaaSCore.Services
{
    public record AuditEntry(
        Guid TenantId,
        Guid? RoomId,
        Guid? FileId,
        string Action,
        string? UserId,
        string? FileName,
        long? FileSize,
        string? FileSha256,
        string? SrcExt,
        string? IpAddress,
        string? UserAgent,
        Guid CorrelationId,
        string? ExtraJson);

    public interface IAuditLogger
    {
        Task LogAsync(AuditEntry entry);
        Guid NewCorrelationId() => Guid.NewGuid();
    }
}