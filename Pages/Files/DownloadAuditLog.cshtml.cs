using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using twoSaaSCore.Services;

namespace twoSaaSCore.Pages.Files
{
    [Authorize]
    public class DownloadAuditLogModel : PageModel
    {
        private readonly ITenantProvider _tenantProvider;
        private readonly IAuditLogger _auditLogger;

        public DownloadAuditLogModel(ITenantProvider tenantProvider, IAuditLogger auditLogger)
        {
            _tenantProvider = tenantProvider;
            _auditLogger = auditLogger;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            // Get all audit entries for this tenant
            var logs = await (_auditLogger as SqlLedgerAuditLogger)?.ExportAuditEntriesAsync(tenantId)
                       ?? new List<AuditEntry>();

            var sb = new StringBuilder();
            // CSV header (added ActionUtc)
            sb.AppendLine("ActionUtc,TenantId,RoomId,FileId,Action,UserId,FileName,FileSize,FileSha256,SrcExt,IpAddress,UserAgent,CorrelationId,ExtraJson");

            foreach (var log in logs)
            {
                // If AuditEntry does not have ActionUtc, update ExportAuditEntriesAsync to include it
                sb.AppendLine(string.Join(",",
                    log.ActionUtc.ToString("o", CultureInfo.InvariantCulture),
                    log.TenantId,
                    log.RoomId,
                    log.FileId,
                    CsvEscape(log.Action),
                    CsvEscape(log.UserId),
                    CsvEscape(log.FileName),
                    log.FileSize,
                    CsvEscape(log.FileSha256),
                    CsvEscape(log.SrcExt),
                    CsvEscape(log.IpAddress),
                    CsvEscape(log.UserAgent),
                    log.CorrelationId,
                    CsvEscape(log.ExtraJson)
                ));
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var fileName = $"AuditLog_{tenantId}_{DateTime.UtcNow:yyyyMMddHHmmss}.csv";
            return File(bytes, "text/csv", fileName);
        }

        private static string CsvEscape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
                return $"\"{value.Replace("\"", "\"\"")}\"";
            return value;
        }
    }
}