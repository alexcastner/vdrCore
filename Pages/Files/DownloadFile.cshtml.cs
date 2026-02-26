using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Syncfusion.Drawing;
using Syncfusion.Pdf;
using Syncfusion.Pdf.Graphics;
using Syncfusion.Pdf.Parsing;
using twoSaaSCore.Models;
using twoSaaSCore.Services;

namespace twoSaaSCore.Pages.Files
{
    [Authorize]
    public class DownloadFileModel : PageModel
    {
        private readonly ITenantProvider _tenantProvider;
        private readonly IRoomFileCatalog _catalog;
        private readonly IRoomPermissionService _permissions;
        private readonly IAuditLogger _auditLogger;
        private readonly UserManager<ApplicationUser> _userManager;

        public DownloadFileModel(ITenantProvider tenantProvider,
                                 IRoomFileCatalog catalog,
                                 IRoomPermissionService permissions,
                                 IAuditLogger auditLogger,
                                 UserManager<ApplicationUser> userManager)
        {
            _tenantProvider = tenantProvider;
            _catalog = catalog;
            _permissions = permissions;
            _auditLogger = auditLogger;
            _userManager = userManager;
        }

        // Single file watermarked download
        public async Task<IActionResult> OnGetAsync(Guid roomId, Guid fileId, string? folderPath)
        {
            if (roomId == Guid.Empty || fileId == Guid.Empty) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            if (!await _permissions.HasPermissionAsync(tenantId, roomId, userId, RoomPermission.Download, folderPath))
                return Forbid();

            var metadata = await _catalog.GetFileAsync(tenantId, roomId, fileId, folderPath);
            if (metadata == null) return NotFound();

            var user = await _userManager.FindByIdAsync(userId);
            var email = user?.Email ?? userId;
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // Log audit
            await _auditLogger.LogAsync(new AuditEntry(
                tenantId, roomId, fileId, "Download", userId, email,
                metadata.OriginalFileName, metadata.Size, null,
                Path.GetExtension(metadata.OriginalFileName)?.ToLowerInvariant(),
                ip, Request.Headers.UserAgent.ToString().Truncate(256),
                _auditLogger.NewCorrelationId(), null));

            var ext = Path.GetExtension(metadata.OriginalFileName).ToLowerInvariant();

            // For PDFs, apply watermark
            if (ext == ".pdf")
            {
                var sasUri = await _catalog.GetReadSasAsync(metadata.BlobName, TimeSpan.FromMinutes(3));
                using var httpClient = new System.Net.Http.HttpClient();
                await using var sourceStream = await httpClient.GetStreamAsync(sasUri);

                var ms = new MemoryStream();
                await sourceStream.CopyToAsync(ms);
                ms.Position = 0;

                var watermarked = ApplyPdfWatermark(ms, email, ip);
                return File(watermarked, "application/pdf", metadata.OriginalFileName);
            }

            // Non-PDF: generate read SAS and redirect
            var readSas = await _catalog.GetReadSasAsync(metadata.BlobName, TimeSpan.FromMinutes(5));
            return Redirect(readSas);
        }

        // Bulk ZIP download (POST with multiple fileIds)
        public async Task<IActionResult> OnPostBulkAsync(Guid roomId, string? folderPath, [FromForm] List<Guid> fileIds)
        {
            if (roomId == Guid.Empty || fileIds == null || fileIds.Count == 0) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            if (!await _permissions.HasPermissionAsync(tenantId, roomId, userId, RoomPermission.Download, folderPath))
                return Forbid();

            var user = await _userManager.FindByIdAsync(userId);
            var email = user?.Email ?? userId;
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var httpClient = new System.Net.Http.HttpClient();
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var fid in fileIds)
                {
                    var metadata = await _catalog.GetFileAsync(tenantId, roomId, fid, folderPath);
                    if (metadata == null) continue;

                    // Deduplicate file names
                    var name = metadata.OriginalFileName;
                    if (!usedNames.Add(name))
                    {
                        var baseName = Path.GetFileNameWithoutExtension(name);
                        var ext = Path.GetExtension(name);
                        var counter = 2;
                        do { name = $"{baseName} ({counter++}){ext}"; } while (!usedNames.Add(name));
                    }

                    var sasUri = await _catalog.GetReadSasAsync(metadata.BlobName, TimeSpan.FromMinutes(5));
                    await using var fileStream = await httpClient.GetStreamAsync(sasUri);

                    var pdfExt = Path.GetExtension(metadata.OriginalFileName).ToLowerInvariant();
                    if (pdfExt == ".pdf")
                    {
                        // Watermark PDFs inside the ZIP
                        var ms = new MemoryStream();
                        await fileStream.CopyToAsync(ms);
                        ms.Position = 0;
                        var watermarked = ApplyPdfWatermark(ms, email, ip);

                        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                        await using var entryStream = entry.Open();
                        watermarked.Position = 0;
                        await watermarked.CopyToAsync(entryStream);
                    }
                    else
                    {
                        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                        await using var entryStream = entry.Open();
                        await fileStream.CopyToAsync(entryStream);
                    }

                    // Log each file download
                    await _auditLogger.LogAsync(new AuditEntry(
                        tenantId, roomId, fid, "BulkDownload", userId, email,
                        metadata.OriginalFileName, metadata.Size, null,
                        Path.GetExtension(metadata.OriginalFileName)?.ToLowerInvariant(),
                        ip, Request.Headers.UserAgent.ToString().Truncate(256),
                        _auditLogger.NewCorrelationId(), null));
                }
            }

            zipStream.Position = 0;
            return File(zipStream, "application/zip", $"room-download-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        }

        private static MemoryStream ApplyPdfWatermark(Stream pdfStream, string userEmail, string ipAddress)
        {
            using var doc = new PdfLoadedDocument(pdfStream);
            var watermarkText = $"{userEmail}  |  {ipAddress}  |  {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
            var font = new PdfStandardFont(PdfFontFamily.Helvetica, 9f);
            var brush = new PdfSolidBrush(new PdfColor(180, 180, 180));

            foreach (PdfPageBase page in doc.Pages)
            {
                var gfx = page.Graphics;
                var state = gfx.Save();

                // Diagonal center watermark
                gfx.SetTransparency(0.15f);
                var largeFont = new PdfStandardFont(PdfFontFamily.Helvetica, 40f);
                var size = largeFont.MeasureString("CONFIDENTIAL");
                gfx.TranslateTransform(page.Size.Width / 2, page.Size.Height / 2);
                gfx.RotateTransform(-45);
                gfx.DrawString("CONFIDENTIAL", largeFont, brush, new PointF(-size.Width / 2, -size.Height / 2));

                gfx.Restore(state);

                // Footer bar with user info
                gfx.SetTransparency(0.4f);
                var textSize = font.MeasureString(watermarkText);
                gfx.DrawString(watermarkText, font, brush,
                    new PointF(5, page.Size.Height - textSize.Height - 5));
            }

            var output = new MemoryStream();
            doc.Save(output);
            output.Position = 0;
            return output;
        }
    }
}
