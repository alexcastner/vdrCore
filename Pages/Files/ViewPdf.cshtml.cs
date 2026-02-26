using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using twoSaaSCore.Models;
using twoSaaSCore.Services;

namespace twoSaaSCore.Pages.Files
{
    [Authorize]
    public class ViewPdfModel : PageModel
    {
        private readonly ITenantProvider _tenantProvider;
        private readonly IRoomFileCatalog _catalog;
        private readonly IRoomPermissionService _permissions;
        private readonly IAuditLogger _auditLogger;
        private readonly UserManager<ApplicationUser> _userManager;

        public ViewPdfModel(ITenantProvider tenantProvider, IRoomFileCatalog catalog, IRoomPermissionService permissions, IAuditLogger auditLogger, UserManager<ApplicationUser> userManager)
        {
            _tenantProvider = tenantProvider;
            _catalog = catalog;
            _permissions = permissions;
            _auditLogger = auditLogger;
            _userManager = userManager;
        }

        [BindProperty(SupportsGet = true)]
        public Guid RoomId { get; set; }

        [BindProperty(SupportsGet = true)]
        public Guid FileId { get; set; }

        public string FileName { get; private set; } = string.Empty;
        public string PdfSourceUrl { get; private set; } = string.Empty;

        public async Task<IActionResult> OnGetAsync()
        {
            if (RoomId == Guid.Empty || FileId == Guid.Empty) return BadRequest();

            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            if (!await _permissions.HasPermissionAsync(tenantId, RoomId, userId, RoomPermission.ViewDocuments))
                return Forbid();

            var metadata = await _catalog.GetFileAsync(tenantId, RoomId, FileId);
            if (metadata == null) return NotFound();
            if (!IsPdf(Uri.UnescapeDataString(metadata.OriginalFileName), metadata.ContentType))
                return BadRequest("Not a PDF.");

            FileName = Uri.UnescapeDataString(metadata.OriginalFileName);

            try
            {
                var sas = await _catalog.GetReadSasAsync(metadata.BlobName, TimeSpan.FromMinutes(3));
                PdfSourceUrl = AppendQuery(sas, $"fid={FileId}");
            }
            catch
            {
                return StatusCode(500, "Failed to generate SAS.");
            }

            var userEmail = (await _userManager.FindByIdAsync(userId))?.Email;

            await _auditLogger.LogAsync(new AuditEntry(
                tenantId, RoomId, FileId, "View", userId, userEmail,
                FileName, metadata.Size, null,
                Path.GetExtension(FileName)?.ToLowerInvariant(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString().Truncate(256),
                _auditLogger.NewCorrelationId(), null));

            return Page();
        }

        private static bool IsPdf(string name, string? contentType)
        {
            var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
            return ext == ".pdf" || (contentType?.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) == true);
        }

        private static string AppendQuery(string uri, string q)
            => uri.Contains('?') ? $"{uri}&{q}" : $"{uri}?{q}";
    }
}