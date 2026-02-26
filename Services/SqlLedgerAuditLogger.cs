using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using twoSaaSCore.Models;

namespace twoSaaSCore.Services
{
    public class SqlLedgerAuditLogger : IAuditLogger
    {
        private readonly string _connStr;
        public SqlLedgerAuditLogger(IConfiguration config)
        {
            _connStr = config.GetConnectionString("DefaultConnection")
                       ?? throw new InvalidOperationException("Missing audit connection string.");
        }

        public async Task LogAsync(AuditEntry e)
        {
            const string sql = @"
INSERT INTO dbo.DocumentAuditLog
(TenantId, RoomId, FileId, UserId, UserEmail, Action, FileName, FileSize, FileSha256,
 IpAddress, UserAgent, CorrelationId, ExtraJson, SrcExt)
VALUES (@TenantId, @RoomId, @FileId, @UserId, @UserEmail, @Action, @FileName, @FileSize, @FileSha256,
        @IpAddress, @UserAgent, @CorrelationId, @ExtraJson, @SrcExt);";

            await using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@TenantId", e.TenantId);
            cmd.Parameters.AddWithValue("@RoomId", (object?)e.RoomId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FileId", (object?)e.FileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UserId", (object?)e.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UserEmail", (object?)e.UserEmail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Action", e.Action);
            cmd.Parameters.AddWithValue("@FileName", (object?)e.FileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FileSize", (object?)e.FileSize ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FileSha256", (object?)e.FileSha256 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@IpAddress", (object?)e.IpAddress ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UserAgent", (object?)e.UserAgent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CorrelationId", e.CorrelationId);
            cmd.Parameters.AddWithValue("@ExtraJson", (object?)e.ExtraJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SrcExt", (object?)e.SrcExt ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<List<DocumentAuditLog>> ListAsync(Guid tenantId, Guid roomId, int skip = 0, int take = 100, CancellationToken ct = default)
        {
            const string sql = @"
SELECT AuditLogId, ActionUtc, TenantId, RoomId, FileId, UserId, UserEmail, Action, FileName, FileSize,
       FileSha256, IpAddress, UserAgent, CorrelationId, ExtraJson, SrcExt
FROM dbo.DocumentAuditLog
WHERE TenantId = @TenantId AND RoomId = @RoomId
ORDER BY ActionUtc DESC
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;";

            var results = new List<DocumentAuditLog>();
            await using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@TenantId", tenantId);
            cmd.Parameters.AddWithValue("@RoomId", roomId);
            cmd.Parameters.AddWithValue("@Skip", skip);
            cmd.Parameters.AddWithValue("@Take", take);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new DocumentAuditLog
                {
                    AuditLogId = reader.GetInt64(0),
                    ActionUtc = reader.GetDateTime(1),
                    TenantId = reader.GetGuid(2),
                    RoomId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    FileId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    UserId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    UserEmail = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Action = reader.GetString(7),
                    FileName = reader.IsDBNull(8) ? null : reader.GetString(8),
                    FileSize = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    FileSha256 = reader.IsDBNull(10) ? null : reader.GetString(10),
                    IpAddress = reader.IsDBNull(11) ? null : reader.GetString(11),
                    UserAgent = reader.IsDBNull(12) ? null : reader.GetString(12),
                    CorrelationId = reader.GetGuid(13),
                    ExtraJson = reader.IsDBNull(14) ? null : reader.GetString(14),
                    SrcExt = reader.IsDBNull(15) ? null : reader.GetString(15)
                });
            }
            return results;
        }
    }
}