using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using twoSaaSCore.Models;

namespace twoSaaSCore.Services
{
    public record AuditEntry(
        Guid TenantId,
        Guid? RoomId,
        Guid? FileId,
        string Action,
        string? UserId,
        string? UserEmail,
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
        Task<List<DocumentAuditLog>> ListAsync(Guid tenantId, Guid roomId, int skip = 0, int take = 100, CancellationToken ct = default);
    }
}