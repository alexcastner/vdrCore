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
using Microsoft.Extensions.Options;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
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
        private readonly DocumentConversionOptions _docOptions;

        public DownloadFileModel(ITenantProvider tenantProvider,
                                 IRoomFileCatalog catalog,
                                 IRoomPermissionService permissions,
                                 IAuditLogger auditLogger,
                                 UserManager<ApplicationUser> userManager,
                                 IOptions<DocumentConversionOptions> docOptions)
        {
            _tenantProvider = tenantProvider;
            _catalog = catalog;
            _permissions = permissions;
            _auditLogger = auditLogger;
            _userManager = userManager;
            _docOptions = docOptions.Value;
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

                var resolvedWatermark = _docOptions.ResolveWatermark(userId, email, tenantId.ToString(), ip);
                var watermarked = ApplyPdfWatermark(ms, resolvedWatermark);
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
                        var resolvedWatermark = _docOptions.ResolveWatermark(userId, email, tenantId.ToString(), ip);
                        var watermarked = ApplyPdfWatermark(ms, resolvedWatermark);

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

        private static MemoryStream ApplyPdfWatermark(Stream pdfStream, string watermarkText)
        {
            using var doc = PdfReader.Open(pdfStream, PdfDocumentOpenMode.Modify);
            var footerFont = new XFont("Helvetica", 9);

            foreach (var page in doc.Pages)
            {
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                var pageWidth = page.Width.Point;
                var pageHeight = page.Height.Point;

                // Diagonal watermark — auto-scale font to fit within 80% of the page diagonal
                var diagonal = Math.Sqrt(pageWidth * pageWidth + pageHeight * pageHeight);
                var maxTextWidth = diagonal * 0.80;

                var fontSize = 40.0;
                var diagonalFont = new XFont("Helvetica", fontSize);
                var textSize = gfx.MeasureString(watermarkText, diagonalFont);

                if (textSize.Width > maxTextWidth)
                {
                    fontSize = fontSize * maxTextWidth / textSize.Width;
                    fontSize = Math.Max(fontSize, 8); // floor at 8pt for readability
                    diagonalFont = new XFont("Helvetica", fontSize);
                    textSize = gfx.MeasureString(watermarkText, diagonalFont);
                }

                var confBrush = new XSolidBrush(XColor.FromArgb(38, 180, 180, 180)); // ~15% opacity
                var state = gfx.Save();
                gfx.TranslateTransform(pageWidth / 2, pageHeight / 2);
                gfx.RotateTransform(-45);
                gfx.DrawString(watermarkText, diagonalFont, confBrush,
                    new XPoint(-textSize.Width / 2, textSize.Height / 2));
                gfx.Restore(state);

                // Footer bar with same text (~40% opacity)
                var footerBrush = new XSolidBrush(XColor.FromArgb(102, 180, 180, 180));
                gfx.DrawString(watermarkText, footerFont, footerBrush,
                    new XPoint(5, pageHeight - 5));
            }

            var output = new MemoryStream();
            doc.Save(output, false);
            output.Position = 0;
            return output;
        }
    }
}
