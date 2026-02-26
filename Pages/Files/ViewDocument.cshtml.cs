using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
using Syncfusion.DocIORenderer;
using Syncfusion.Pdf;
using Syncfusion.XlsIO;
using Syncfusion.XlsIORenderer;
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

        private const long MaxOfficeBytesForConversion = 25 * 1024 * 1024; // shared cap

        public ViewDocumentModel(ITenantProvider tenantProvider, IRoomFileCatalog catalog, IFileStorage storage, IAuditLogger auditLogger, IRoomPermissionService permissions, UserManager<ApplicationUser> userManager)
        {
            _tenantProvider = tenantProvider;
            _catalog = catalog;
            _storage = storage;
            _auditLogger = auditLogger;
            _permissions = permissions;
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

            var originalName = Uri.UnescapeDataString(metadata.OriginalFileName);
            FileName = originalName;

            if (IsPdf(originalName, metadata.ContentType))
                return RedirectToPage("/Files/ViewPdf", new { RoomId, FileId });

            if (!IsConvertibleToPdf(originalName, metadata.ContentType))
                return BadRequest("Unsupported format. Only .docx or .xlsx can be converted.");

            var cacheBlobName = BuildCacheBlobName(metadata.BlobName, FileId);

            var cacheValid = false;
            const string tagSourceBlob = "SrcBlob";
            const string tagSourceSize = "SrcSize";

            try
            {
                var tags = await _storage.GetTagsAsync(cacheBlobName);
                if (tags.TryGetValue(tagSourceBlob, out var taggedBlob) &&
                    tags.TryGetValue(tagSourceSize, out var taggedSize) &&
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

            if (!cacheValid)
            {
                if (metadata.Size > MaxOfficeBytesForConversion)
                    return BadRequest($"Document exceeds max convertible size ({MaxOfficeBytesForConversion / (1024 * 1024)} MB).");

                await using var sourceStream = await _storage.OpenReadAsync(metadata.BlobName);
                var mem = PrepareBufferedStream(sourceStream, metadata.Size);

                try
                {
                    await using var pdfMs = await ConvertToPdfAsync(mem, originalName);
                    pdfMs.Position = 0;
                    await _storage.UploadAsync(cacheBlobName, pdfMs, "application/pdf");

                    var tags = new Dictionary<string, string>
                    {
                        { tagSourceBlob, metadata.BlobName },
                        { tagSourceSize, metadata.Size.ToString() },
                        { "CreatedUtc", DateTime.UtcNow.ToString("o") },
                        { "FileId", FileId.ToString() },
                        { "SrcExt", Path.GetExtension(originalName).ToLowerInvariant() }
                    };
                    try { await _storage.SetTagsAsync(cacheBlobName, tags); } catch { /* ignore */ }
                }
                catch
                {
                    return StatusCode(500, "Conversion failed.");
                }
            }

            try
            {
                var sas = await _storage.GenerateBlobReadSasAsync(cacheBlobName, TimeSpan.FromMinutes(3));
                PdfSourceUrl = AppendQuery(sas, $"fid={FileId}");
            }
            catch
            {
                return StatusCode(500, "Failed to generate SAS for cached PDF.");
            }

            var userEmail = (await _userManager.FindByIdAsync(userId))?.Email;

            await _auditLogger.LogAsync(new AuditEntry(
                tenantId,
                RoomId,
                FileId,
                "View",
                userId,
                userEmail,
                FileName,
                metadata.Size,
                null,//metadata.HashSha256, // if you store it; else null
                Path.GetExtension(FileName)?.ToLowerInvariant(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString().Truncate(256),
                _auditLogger.NewCorrelationId(),
                null));

            return Page();
        }

        public Task<IActionResult> OnGetContentAsync() =>
            Task.FromResult<IActionResult>(NotFound());

        private static MemoryStream PrepareBufferedStream(Stream source, long sizeHint)
        {
            MemoryStream mem;
            if (sizeHint > 0 && sizeHint <= int.MaxValue)
                mem = new MemoryStream((int)sizeHint);
            else
                mem = new MemoryStream();

            source.CopyTo(mem);
            mem.Position = 0;
            return mem;
        }

        // Then, fully qualify the ExcelEngine usage in ConvertToPdfAsync:
        private static async Task<MemoryStream> ConvertToPdfAsync(Stream officeStream, string originalName)
        {
            var ext = Path.GetExtension(originalName).ToLowerInvariant();
            if (ext == ".docx")
            {
                using var wordDoc = new WordDocument(officeStream, FormatType.Automatic);
                using var renderer = new DocIORenderer();
                using var pdfDoc = renderer.ConvertToPDF(wordDoc);
                var ms = new MemoryStream();
                pdfDoc.Save(ms);
                await ms.FlushAsync();
                ms.Position = 0;
                return ms;
            }
            if (ext == ".xlsx")
            {
                // Explicitly specify the namespace to resolve ambiguity
                using var excelEngine = new Syncfusion.XlsIO.ExcelEngine();
                var app = excelEngine.Excel;
                app.DefaultVersion = ExcelVersion.Xlsx;
                officeStream.Position = 0;
                var workbook = app.Workbooks.Open(officeStream);

                //Initialize XlsIO renderer.
                XlsIORenderer renderer = new XlsIORenderer();
                
                PdfDocument pdfDoc = renderer.ConvertToPDF(workbook,new XlsIORendererSettings { LayoutOptions = LayoutOptions.FitSheetOnOnePage});

                var ms = new MemoryStream();
                pdfDoc.Save(ms);
                await ms.FlushAsync();
                ms.Position = 0;
                return ms;
            }
            throw new NotSupportedException("Extension not supported for conversion.");
        }

        private static string BuildCacheBlobName(string originalBlobName, Guid fileId)
            => $"cache/{fileId}.pdf";

        private static string AppendQuery(string uri, string q)
            => uri.Contains('?', StringComparison.Ordinal) ? $"{uri}&{q}" : $"{uri}?{q}";

        private static bool IsPdf(string name, string? contentType)
        {
            var ext = Path.GetExtension(name).ToLowerInvariant();
            return ext == ".pdf" || (contentType?.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) == true);
        }

        private static bool IsDocx(string name, string? contentType)
        {
            var ext = Path.GetExtension(name).ToLowerInvariant();
            return ext == ".docx" || (contentType?.Equals(
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                StringComparison.OrdinalIgnoreCase) == true);
        }

        private static bool IsXlsx(string name, string? contentType)
        {
            var ext = Path.GetExtension(name).ToLowerInvariant();
            return ext == ".xlsx" || (contentType?.Equals(
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                StringComparison.OrdinalIgnoreCase) == true);
        }

        private static bool IsConvertibleToPdf(string name, string? contentType)
            => IsDocx(name, contentType) || IsXlsx(name, contentType);
    }

    public static class StringExtensions
    {
        public static string Truncate(this string? s, int max) =>
            string.IsNullOrEmpty(s) ? string.Empty :
            (s.Length <= max ? s : s.Substring(0, max));
    }
}