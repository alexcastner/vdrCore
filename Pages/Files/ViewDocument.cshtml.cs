using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using twoSaaSCore.Models;
using twoSaaSCore.Services;

namespace twoSaaSCore.Pages.Files
{
    [Authorize]
    public class ViewDocumentModel : PageModel
    {
        private readonly ITenantProvider _tenantProvider;
        private readonly IRoomFileCatalog _catalog;
        private readonly IFileStorage _storage;
        private readonly IAuditLogger _auditLogger;
        private readonly IRoomPermissionService _permissions;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly DocumentConversionOptions _docOptions;

        public ViewDocumentModel(ITenantProvider tenantProvider, IRoomFileCatalog catalog, IFileStorage storage, IAuditLogger auditLogger, IRoomPermissionService permissions, UserManager<ApplicationUser> userManager, IOptions<DocumentConversionOptions> docOptions)
        {
            _tenantProvider = tenantProvider;
            _catalog = catalog;
            _storage = storage;
            _auditLogger = auditLogger;
            _permissions = permissions;
            _userManager = userManager;
            _docOptions = docOptions.Value;
        }

        [BindProperty(SupportsGet = true)]
        public Guid RoomId { get; set; }

        [BindProperty(SupportsGet = true)]
        public Guid FileId { get; set; }

        public string FileName { get; private set; } = string.Empty;
        public string PdfSourceUrl { get; private set; } = string.Empty;
        public string WatermarkText { get; private set; } = string.Empty;

        /// <summary>When true the view should show a "download only" message instead of the PDF viewer.</summary>
        public bool ShowDownloadFallback { get; private set; }

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

            var originalName = Uri.UnescapeDataString(metadata.OriginalFileName);
            FileName = originalName;

            if (IsPdf(originalName, metadata.ContentType))
                return RedirectToPage("/Files/ViewPdf", new { RoomId, FileId });

            if (!IsOfficeFormat(originalName, metadata.ContentType))
                return BadRequest("Unsupported format for viewing.");

            // Try to serve a previously cached PDF conversion
            var cacheBlobName = BuildCacheBlobName(metadata.BlobName, FileId);
            var cacheValid = false;

            try
            {
                var tags = await _storage.GetTagsAsync(cacheBlobName);
                if (tags.TryGetValue("SrcBlob", out var taggedBlob) &&
                    tags.TryGetValue("SrcSize", out var taggedSize) &&
                    taggedBlob == metadata.BlobName &&
                    taggedSize == metadata.Size.ToString())
                {
                    cacheValid = true;
                }
            }
            catch
            {
                cacheValid = false;
            }

            if (cacheValid)
            {
                try
                {
                    var sas = await _storage.GenerateBlobReadSasAsync(cacheBlobName, TimeSpan.FromMinutes(3));
                    PdfSourceUrl = AppendQuery(sas, $"fid={FileId}");
                }
                catch
                {
                    cacheValid = false;
                }
            }

            if (!cacheValid)
            {
                // No cached conversion available — show download fallback
                ShowDownloadFallback = true;
            }

            var userEmail = (await _userManager.FindByIdAsync(userId))?.Email;

            WatermarkText = _docOptions.ResolveWatermark(
                userId, userEmail, tenantId.ToString(),
                HttpContext.Connection.RemoteIpAddress?.ToString());

            await _auditLogger.LogAsync(new AuditEntry(
                tenantId,
                RoomId,
                FileId,
                "View",
                userId,
                userEmail,
                FileName,
                metadata.Size,
                null,
                Path.GetExtension(FileName)?.ToLowerInvariant(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString().Truncate(256),
                _auditLogger.NewCorrelationId(),
                null));

            return Page();
        }

        public Task<IActionResult> OnGetContentAsync() =>
            Task.FromResult<IActionResult>(NotFound());

        private static string BuildCacheBlobName(string originalBlobName, Guid fileId)
            => $"cache/{fileId}.pdf";

        private static string AppendQuery(string uri, string q)
            => uri.Contains('?', StringComparison.Ordinal) ? $"{uri}&{q}" : $"{uri}?{q}";

        private static bool IsPdf(string name, string? contentType)
        {
            var ext = Path.GetExtension(name).ToLowerInvariant();
            return ext == ".pdf" || (contentType?.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) == true);
        }

        private static bool IsOfficeFormat(string name, string? contentType)
        {
            var ext = Path.GetExtension(name).ToLowerInvariant();
            return ext is ".docx" or ".doc" or ".xlsx" or ".xls";
        }
    }

    public static class StringExtensions
    {
        public static string Truncate(this string? s, int max) =>
            string.IsNullOrEmpty(s) ? string.Empty :
            (s.Length <= max ? s : s.Substring(0, max));
    }
}