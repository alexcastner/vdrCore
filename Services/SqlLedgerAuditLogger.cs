using System;
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
    }
}