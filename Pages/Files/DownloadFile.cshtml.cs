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
using Syncfusion.XlsIO;
using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
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

            // For Excel files, apply watermark
            if (ext is ".xlsx" or ".xls")
            {
                var sasUri = await _catalog.GetReadSasAsync(metadata.BlobName, TimeSpan.FromMinutes(3));
                using var httpClient = new System.Net.Http.HttpClient();
                await using var sourceStream = await httpClient.GetStreamAsync(sasUri);

                var ms = new MemoryStream();
                await sourceStream.CopyToAsync(ms);
                ms.Position = 0;

                var resolvedWatermark = _docOptions.ResolveWatermark(userId, email, tenantId.ToString(), ip);
                var watermarked = ApplyXlsxWatermark(ms, resolvedWatermark);
                return File(watermarked,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    metadata.OriginalFileName);
            }

            // For Word documents, apply watermark
            if (ext is ".docx" or ".doc")
            {
                var sasUri = await _catalog.GetReadSasAsync(metadata.BlobName, TimeSpan.FromMinutes(3));
                using var httpClient = new System.Net.Http.HttpClient();
                await using var sourceStream = await httpClient.GetStreamAsync(sasUri);

                var ms = new MemoryStream();
                await sourceStream.CopyToAsync(ms);
                ms.Position = 0;

                var resolvedWatermark = _docOptions.ResolveWatermark(userId, email, tenantId.ToString(), ip);
                var watermarked = ApplyDocxWatermark(ms, resolvedWatermark);
                return File(watermarked,
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    metadata.OriginalFileName);
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
                    else if (pdfExt is ".xlsx" or ".xls")
                    {
                        // Watermark Excel files inside the ZIP
                        var ms = new MemoryStream();
                        await fileStream.CopyToAsync(ms);
                        ms.Position = 0;
                        var resolvedWatermark = _docOptions.ResolveWatermark(userId, email, tenantId.ToString(), ip);
                        var watermarked = ApplyXlsxWatermark(ms, resolvedWatermark);

                        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                        await using var entryStream = entry.Open();
                        watermarked.Position = 0;
                        await watermarked.CopyToAsync(entryStream);
                    }
                    else if (pdfExt is ".docx" or ".doc")
                    {
                        // Watermark Word documents inside the ZIP
                        var ms = new MemoryStream();
                        await fileStream.CopyToAsync(ms);
                        ms.Position = 0;
                        var resolvedWatermark = _docOptions.ResolveWatermark(userId, email, tenantId.ToString(), ip);
                        var watermarked = ApplyDocxWatermark(ms, resolvedWatermark);

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

        /// <summary>
        /// Adds a watermark to every worksheet in an XLSX workbook.
        /// Sets header/footer text (visible when printed) and inserts a
        /// light-gray watermark row at the top of each sheet.
        /// </summary>
        private static MemoryStream ApplyXlsxWatermark(Stream xlsxStream, string watermarkText)
        {
            using var engine = new ExcelEngine();
            var application = engine.Excel;
            application.DefaultVersion = ExcelVersion.Xlsx;

            var workbook = application.Workbooks.Open(xlsxStream);

            foreach (IWorksheet worksheet in workbook.Worksheets)
            {
                // Print header/footer watermark (visible in print preview and printouts)
                worksheet.PageSetup.CenterHeader = watermarkText;
                worksheet.PageSetup.LeftFooter = watermarkText;

                // Insert a visible watermark row at the top of the sheet
                worksheet.InsertRow(1, 1);
                var lastCol = worksheet.UsedRange?.LastColumn ?? 1;
                var cols = Math.Max(lastCol, 5);

                var watermarkCell = worksheet.Range[1, 1];
                watermarkCell.Text = watermarkText;
                watermarkCell.CellStyle.Font.RGBColor = Syncfusion.Drawing.Color.FromArgb(180, 180, 180);
                watermarkCell.CellStyle.Font.Size = 9;
                watermarkCell.CellStyle.Font.Italic = true;
                watermarkCell.CellStyle.HorizontalAlignment = ExcelHAlign.HAlignCenter;

                var mergeRange = worksheet.Range[1, 1, 1, cols];
                mergeRange.Merge();
                mergeRange.CellStyle.Color = Syncfusion.Drawing.Color.FromArgb(245, 245, 245);
                mergeRange.CellStyle.Borders[ExcelBordersIndex.EdgeBottom].LineStyle = ExcelLineStyle.Thin;
                mergeRange.CellStyle.Borders[ExcelBordersIndex.EdgeBottom].ColorRGB = Syncfusion.Drawing.Color.FromArgb(220, 220, 220);

                worksheet.SetRowHeight(1, 18);
            }

            var output = new MemoryStream();
            workbook.SaveAs(output);
            output.Position = 0;
            return output;
        }

        /// <summary>
        /// Adds a watermark to a DOCX document.
        /// Uses the built-in diagonal text watermark on each section and
        /// adds a footer paragraph with the watermark text.
        /// </summary>
        private static MemoryStream ApplyDocxWatermark(Stream docxStream, string watermarkText)
        {
            using var document = new WordDocument(docxStream, FormatType.Automatic);

            // Diagonal text watermark (renders behind content, visible on screen and print)
            document.Watermark = new TextWatermark(watermarkText, "Calibri", 250, 40)
            {
                Color = Syncfusion.Drawing.Color.FromArgb(180, 180, 180),
                Semitransparent = true,
                Layout = WatermarkLayout.Diagonal
            };

            foreach (WSection section in document.Sections)
            {
                // Footer with watermark text for forensic traceability
                var footer = section.HeadersFooters.Footer;
                var footerPara = footer.AddParagraph() as WParagraph;
                if (footerPara != null)
                {
                    var run = footerPara.AppendText(watermarkText);
                    run.CharacterFormat.FontName = "Calibri";
                    run.CharacterFormat.FontSize = 9;
                    run.CharacterFormat.Italic = true;
                    run.CharacterFormat.TextColor = Syncfusion.Drawing.Color.FromArgb(180, 180, 180);
                }
            }

            var output = new MemoryStream();
            document.Save(output, FormatType.Docx);
            output.Position = 0;
            return output;
        }
    }
}
