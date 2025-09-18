using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

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
(TenantId, RoomId, FileId, UserId, Action, FileName, FileSize, FileSha256,
 IpAddress, UserAgent, CorrelationId, ExtraJson, SrcExt)
VALUES (@TenantId, @RoomId, @FileId, @UserId, @Action, @FileName, @FileSize, @FileSha256,
        @IpAddress, @UserAgent, @CorrelationId, @ExtraJson, @SrcExt);";

            await using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@TenantId", e.TenantId);
            cmd.Parameters.AddWithValue("@RoomId", (object?)e.RoomId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FileId", (object?)e.FileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UserId", (object?)e.UserId ?? DBNull.Value);
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

        // Export all audit records for a given tenant as AuditEntry records
        public async Task<List<AuditEntry>> ExportAuditEntriesAsync(Guid tenantId)
        {
            var result = new List<AuditEntry>();
            const string sql = @"
SELECT ActionUtc,TenantId, RoomId, FileId, Action, UserId, FileName, FileSize, FileSha256,
       SrcExt, IpAddress, UserAgent, CorrelationId, ExtraJson
FROM dbo.DocumentAuditLog
WHERE TenantId = @TenantId
ORDER BY ActionUtc";

            await using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@TenantId", tenantId);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new AuditEntry(
                    reader.GetDateTime(0), // ActionUtc
                    reader.GetGuid(1), // TenantId
                    reader.IsDBNull(2) ? null : reader.GetGuid(2), // RoomId
                    reader.IsDBNull(3) ? null : reader.GetGuid(3), // FileId
                    reader.GetString(4), // Action
                    reader.IsDBNull(5) ? null : reader.GetString(5), // UserId
                    reader.IsDBNull(6) ? null : reader.GetString(6), // FileName
                    reader.IsDBNull(7) ? null : reader.GetInt64(7), // FileSize
                    reader.IsDBNull(8) ? null : reader.GetString(8), // FileSha256
                    reader.IsDBNull(9) ? null : reader.GetString(9), // SrcExt
                    reader.IsDBNull(10) ? null : reader.GetString(10), // IpAddress
                    reader.IsDBNull(11) ? null : reader.GetString(11), // UserAgent
                    reader.GetGuid(12), // CorrelationId
                    reader.IsDBNull(13) ? null : reader.GetString(13) // ExtraJson
                ));
            }
            return result;
        }
    }
}