using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Options;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
using Syncfusion.XlsIO;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using twoSaaSCore.Services;
using static System.Net.Mime.MediaTypeNames;
using SkiaSharp;
using Syncfusion.XlsIO;
using Syncfusion.Presentation;

namespace twoSaaSCore.Pages.Files
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly ITenantProvider _tenantProvider;
        private readonly IRoomFileCatalog _catalog;
        private readonly AzureBlobOptions _blobOptions;
        private readonly IAuditLogger _auditLogger;

        public IndexModel(ITenantProvider tenantProvider,
                          IRoomFileCatalog catalog,
                          IOptions<AzureBlobOptions> blobOptions, IAuditLogger auditLogger)
        {
            _tenantProvider = tenantProvider;
            _catalog = catalog;
            _blobOptions = blobOptions.Value;
            _auditLogger = auditLogger;
        }

        // Query parameters
        [BindProperty(SupportsGet = true)]
        public Guid? RoomId { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? FolderPath { get; set; } // normalized (may be null/empty or end with /)

        // Room list (when no RoomId)
        public List<RoomRow> Rooms { get; private set; } = new();

        // Folder list (immediate children)
        public List<VirtualFolderRow> Folders { get; private set; } = new();

        // Files (in current folder or root of room)
        public List<FileRow> Files { get; private set; } = new();

        public class RoomRow
        {
            public Guid RoomId { get; set; }
            public string Name { get; set; } = string.Empty;
            public DateTimeOffset CreatedUtc { get; set; }
        }

        public class VirtualFolderRow
        {
            public Guid FolderId { get; set; }
            public string Path { get; set; } = string.Empty;   // always ends with /
            public string Name { get; set; } = string.Empty;
            public DateTimeOffset CreatedUtc { get; set; }
            public string? CreatedBy { get; set; }
        }

        public class FileRow
        {
            public Guid FileId { get; set; }
            public string FileName { get; set; } = string.Empty;
            public long Size { get; set; }
            public DateTimeOffset UploadedUtc { get; set; }
            public string? UploadedBy { get; set; }
            public string BlobName { get; set; } = string.Empty;
        }

        public async Task OnGetAsync()
        {
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return;

            if (RoomId == null || RoomId == Guid.Empty)
            {
                // List rooms
                await foreach (var r in _catalog.ListRoomsAsync(tenantId))
                {
                    Rooms.Add(new RoomRow
                    {
                        RoomId = r.RoomId,
                        Name = r.Name,
                        CreatedUtc = r.CreatedUtc
                    });
                }
                return;
            }

            // Normalize FolderPath (catalog expects trailing slash or empty)
            FolderPath = NormalizeFolderPath(FolderPath);

            // List folders (immediate children)
            await foreach (var vf in _catalog.ListVirtualFoldersAsync(tenantId, RoomId.Value, FolderPath))
            {
                Folders.Add(new VirtualFolderRow
                {
                    FolderId = vf.FolderId,
                    Path = vf.Path,
                    Name = vf.Name,
                    CreatedUtc = vf.CreatedUtc,
                    CreatedBy = vf.CreatedByUserId
                });
            }

            // List files in folder/root
            await foreach (var fm in _catalog.ListFilesAsync(tenantId, RoomId.Value, FolderPath))
            {
                Files.Add(new FileRow
                {
                    FileId = fm.FileId,
                    FileName = fm.OriginalFileName,
                    Size = fm.Size,
                    UploadedUtc = fm.UploadedUtc,
                    UploadedBy = fm.UploadedByUserId,
                    BlobName = fm.BlobName
                });
            }
        }

        // ----- Room Handlers -----

        public async Task<IActionResult> OnPostCreateRoomAsync(string name, string? ndaText)
        {
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty || string.IsNullOrWhiteSpace(name)) return BadRequest();
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            await _catalog.CreateRoomAsync(tenantId, name.Trim(), ndaText, userId);
            return RedirectToPage(new { roomId = (Guid?)null });
        }

        public async Task<IActionResult> OnPostDeleteRoomAsync(Guid roomId)
        {
            if (roomId == Guid.Empty) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();
            await _catalog.DeleteRoomAsync(tenantId, roomId);
            return RedirectToPage(new { roomId = (Guid?)null });
        }

        // ----- Folder Handlers -----

        public async Task<IActionResult> OnPostCreateFolderAsync(Guid roomId, string parentPath, string folderName)
        {
            if (roomId == Guid.Empty || string.IsNullOrWhiteSpace(folderName))
                return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            parentPath = NormalizeFolderPath(parentPath);
            var safeFolderSegment = folderName.Trim();
            // Compose new folder path under parent
            var fullPath = parentPath + safeFolderSegment + "/";
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            await _catalog.CreateVirtualFolderAsync(tenantId, roomId, fullPath, folderName.Trim(), userId);
            return RedirectToPage(new { roomId, folderPath = parentPath });
        }

        public async Task<IActionResult> OnPostDeleteFolderAsync(Guid roomId, string folderPath, bool? recursive)
        {
            if (roomId == Guid.Empty) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            folderPath = NormalizeFolderPath(folderPath);
            if (string.IsNullOrEmpty(folderPath)) return BadRequest();

            // Determine parent path to redirect back
            var parent = GetParentFolderPath(folderPath);
            await _catalog.DeleteVirtualFolderAsync(tenantId, roomId, folderPath, recursive == true);
            return RedirectToPage(new { roomId, folderPath = parent });
        }

        // ----- File Upload (Small) -----

        public async Task<IActionResult> OnPostUploadAsync(IFormFile? file, Guid roomId, string? folderPath)
        {
            if (file == null || file.Length == 0) return BadRequest("Select a file.");
            if (roomId == Guid.Empty) return BadRequest("roomId required.");
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            folderPath = NormalizeFolderPath(folderPath);
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            await using var s = file.OpenReadStream();
            await _catalog.StoreFileAsync(tenantId, roomId, file.FileName, s, file.ContentType, userId, folderPath);
            return RedirectToPage(new { roomId, folderPath });
        }

        // ----- Large Upload Init / Finalize -----

        public async Task<IActionResult> OnPostInitLargeAsync([FromForm] Guid roomId,
                                                              [FromForm] string fileName,
                                                              [FromForm] string? folderPath)
        {
            if (roomId == Guid.Empty || string.IsNullOrWhiteSpace(fileName)) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();
            folderPath = NormalizeFolderPath(folderPath);
            var (blobName, sas) = await _catalog.GetWriteSasAsync(tenantId, roomId, fileName, TimeSpan.FromMinutes(15), folderPath);
            return new JsonResult(new { blobName, sas });
        }

        public async Task<IActionResult> OnPostFinalizeLargeAsync(
            [FromForm] Guid roomId,
            [FromForm] string blobName,
            [FromForm] string fileName,
            [FromForm] long size,
            [FromForm] string? contentType,
            [FromForm] string? folderPath)
        {
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();
            if (roomId == Guid.Empty || string.IsNullOrWhiteSpace(blobName) || string.IsNullOrWhiteSpace(fileName))
                return BadRequest();

            folderPath = NormalizeFolderPath(folderPath);

            // Validate prefix: tenantId/roomId/(folderPath)file
            var expectedPrefix = $"{tenantId}/{roomId}/";
            if (!blobName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                return BadRequest("Invalid blob path.");
            if (!string.IsNullOrEmpty(folderPath) && !blobName.StartsWith(expectedPrefix + folderPath, StringComparison.OrdinalIgnoreCase))
                return BadRequest("Folder path mismatch.");

            var fileId = ExtractFileId(blobName);
            if (fileId == Guid.Empty) return BadRequest("Invalid blob name format.");

            // Set tags (uploaded by SAS)
            var service = new BlobServiceClient(_blobOptions.ConnectionString);
            var container = service.GetBlobContainerClient(_blobOptions.Container);
            var blob = container.GetBlobClient(blobName);

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            var tags = new Dictionary<string, string>
            {
                {"tenantId", tenantId.ToString() },
                {"roomId", roomId.ToString() },
                {"fileId", fileId.ToString() },
                {"uploadedBy", TruncateForTag(userId) },
                {"uploadedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() },
                {"size", size.ToString() },
                {"folderPath", folderPath ?? "" },
                {"fileNameEnc", EncodeFileNameForTag(fileName) }
            };

            try
            {
                await blob.SetTagsAsync(tags);
            }
            catch
            {
                return StatusCode(500, "Failed to set tags. Retry finalize.");
            }

            return new JsonResult(new { fileId });
        }

        // ----- Delete File -----

        public async Task<IActionResult> OnPostDeleteFileAsync(Guid roomId, Guid fileId, string? folderPath)
        {
            if (roomId == Guid.Empty || fileId == Guid.Empty) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();


            var metadata = await _catalog.GetFileAsync(tenantId, roomId, fileId);
            if (metadata == null) return NotFound();

            var originalName = Uri.UnescapeDataString(metadata.OriginalFileName);
            
            folderPath = NormalizeFolderPath(folderPath);
            await _catalog.DeleteFileAsync(tenantId, roomId, fileId, folderPath);
            await _auditLogger.LogAsync(new AuditEntry(DateTime.UtcNow,
              tenantId,
              RoomId,
              fileId,
              "View",
              User.Identity?.Name,
              originalName,
              metadata.Size,
              null,//metadata.HashSha256, // if you store it; else null
              Path.GetExtension(originalName)?.ToLowerInvariant(),
              HttpContext.Connection.RemoteIpAddress?.ToString(),
              Request.Headers.UserAgent.ToString().Truncate(256),
              _auditLogger.NewCorrelationId(),
              null));
            return RedirectToPage(new { roomId, folderPath });
        }

        // ----- Download File -----

        public async Task<IActionResult> OnGetDownloadAsync(Guid roomId, Guid fileId)
        {
            if (roomId == Guid.Empty || fileId == Guid.Empty) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            var metadata = await _catalog.GetFileAsync(tenantId, roomId, fileId);
            if (metadata == null) return NotFound();

            var originalName = Uri.UnescapeDataString(metadata.OriginalFileName);
            var ext = Path.GetExtension(originalName)?.ToLowerInvariant();

            var service = new BlobServiceClient(_blobOptions.ConnectionString);
            var container = service.GetBlobContainerClient(_blobOptions.Container);
            var blob = container.GetBlobClient(metadata.BlobName);

            if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                await using var srcStream = await blob.OpenReadAsync();
                using var inMs = new MemoryStream();
                await srcStream.CopyToAsync(inMs);
                inMs.Position = 0;

                using var doc = PdfReader.Open(inMs, PdfDocumentOpenMode.Modify);
                var watermarkText = $"CONFIDENTIAL • {User.Identity?.Name ?? "user"} • {DateTime.UtcNow:u}";

                foreach (var page in doc.Pages)
                {
                    using var gfx = XGraphics.FromPdfPage(page);
                    var font = new XFont("Arial", 48, XFontStyle.Bold);
                    var brush = new XSolidBrush(XColor.FromArgb(64, 255, 0, 0));
                    var center = new XPoint(page.Width / 2, page.Height / 2);

                    gfx.TranslateTransform(center.X, center.Y);
                    gfx.RotateTransform(-25);
                    gfx.DrawString(watermarkText, font, brush, new XPoint(0, 0), XStringFormats.Center);
                }

                var outMs = new MemoryStream();
                doc.Save(outMs);
                outMs.Position = 0;

                await _auditLogger.LogAsync(new AuditEntry(
                    DateTime.UtcNow, tenantId, RoomId, fileId, "Download", User.Identity?.Name,
                    originalName, metadata.Size, null, Path.GetExtension(originalName)?.ToLowerInvariant(),
                    HttpContext.Connection.RemoteIpAddress?.ToString(),
                    Request.Headers.UserAgent.ToString().Truncate(256),
                    _auditLogger.NewCorrelationId(), null));

                return File(outMs, "application/pdf", originalName);
            }
            else if (string.Equals(ext, ".docx", StringComparison.OrdinalIgnoreCase))
            {
                await using var srcStream = await blob.OpenReadAsync();
                using var inMs = new MemoryStream();
                await srcStream.CopyToAsync(inMs);
                inMs.Position = 0;

                using var wordDoc = new WordDocument(inMs, Syncfusion.DocIO.FormatType.Automatic);

                var watermarkText = $"CONFIDENTIAL • {User.Identity?.Name ?? "user"} • {DateTime.UtcNow:u}";
                var textWatermark = new TextWatermark(watermarkText,"Arial",1000,48)
                {
                    Color = Syncfusion.Drawing.Color.LightGray,
                    Layout = WatermarkLayout.Diagonal,
                    Semitransparent = true,
                    Size = 48
                };
                wordDoc.Watermark = textWatermark;

                var outMs = new MemoryStream();
                wordDoc.Save(outMs, Syncfusion.DocIO.FormatType.Docx);
                outMs.Position = 0;

                await _auditLogger.LogAsync(new AuditEntry(
                    DateTime.UtcNow, tenantId, RoomId, fileId, "Download", User.Identity?.Name,
                    originalName, metadata.Size, null, Path.GetExtension(originalName)?.ToLowerInvariant(),
                    HttpContext.Connection.RemoteIpAddress?.ToString(),
                    Request.Headers.UserAgent.ToString().Truncate(256),
                    _auditLogger.NewCorrelationId(), null));

                return File(outMs, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", originalName);
            }
            else if (string.Equals(ext, ".pptx", StringComparison.OrdinalIgnoreCase))
            {
                // NEW: PPTX watermark (PNG overlay on each slide)
                await using var srcStream = await blob.OpenReadAsync();
                using var inMs = new MemoryStream();
                await srcStream.CopyToAsync(inMs);
                inMs.Position = 0;

                using var presentation = Presentation.Open(inMs);
                var watermarkText = $"CONFIDENTIAL • {User.Identity?.Name ?? "user"} • {DateTime.UtcNow:u}";
                var pngBytes = CreateWatermarkPngSkia(watermarkText, width: 1600, height: 1100, angleDeg: -30f, opacity: 0.16f);

                // Add image to presentation image collection once
                using var imgStream = new MemoryStream(pngBytes);
                

                foreach (ISlide slide in presentation.Slides)
                {
                    // Place large centered image; adjust margins as needed
                    var left = 40;
                    var top = 40;
                    var width = 1200;
                    var height = 800;

                    var picture = slide.Shapes.AddPicture(imgStream, left, top, width, height);
                    // Send under slide content; prints and shows in normal view
                    //picture.SendToBack(); NOT SUPPORTED in Syncfusion
                }

                var outMs = new MemoryStream();
                presentation.Save(outMs);
                outMs.Position = 0;

                await _auditLogger.LogAsync(new AuditEntry(
                    DateTime.UtcNow, tenantId, RoomId, fileId, "Download", User.Identity?.Name,
                    originalName, metadata.Size, null, Path.GetExtension(originalName)?.ToLowerInvariant(),
                    HttpContext.Connection.RemoteIpAddress?.ToString(),
                    Request.Headers.UserAgent.ToString().Truncate(256),
                    _auditLogger.NewCorrelationId(), null));

                return File(outMs, "application/vnd.openxmlformats-officedocument.presentationml.presentation", originalName);
            }
            else if (string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                // ... existing XLSX watermark branch (Skia PNG + worksheet picture) unchanged ...
                await using var srcStream = await blob.OpenReadAsync();
                using var inMs = new MemoryStream();
                await srcStream.CopyToAsync(inMs);
                inMs.Position = 0;

                using var excelEngine = new ExcelEngine();
                var app = excelEngine.Excel;
                app.DefaultVersion = ExcelVersion.Xlsx;
                IWorkbook workbook = app.Workbooks.Open(inMs);

                var watermarkText = $"CONFIDENTIAL • {User.Identity?.Name ?? "user"} • {DateTime.UtcNow:u}";
                var pngBytes = CreateWatermarkPngSkia(watermarkText, width: 1600, height: 1100, angleDeg: -35f, opacity: 0.18f);

                foreach (IWorksheet sheet in workbook.Worksheets)
                {
                    using var imgStream2 = new MemoryStream(pngBytes);
                    var picture = sheet.Pictures.AddPicture(1, 1, imgStream2);
                    picture.Width = 1200;
                    picture.Height = 800;
                    picture.Top = 80;
                    picture.Left = 40;
                }

                var outMs = new MemoryStream();
                workbook.SaveAs(outMs);
                outMs.Position = 0;

                await _auditLogger.LogAsync(new AuditEntry(
                    DateTime.UtcNow, tenantId, RoomId, fileId, "Download", User.Identity?.Name,
                    originalName, metadata.Size, null, Path.GetExtension(originalName)?.ToLowerInvariant(),
                    HttpContext.Connection.RemoteIpAddress?.ToString(),
                    Request.Headers.UserAgent.ToString().Truncate(256),
                    _auditLogger.NewCorrelationId(), null));

                return File(outMs, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", originalName);
            }
            else if (string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                // NEW: Image watermark (burn text into pixels)
                await using var srcStream = await blob.OpenReadAsync();
                using var inMs = new MemoryStream();
                await srcStream.CopyToAsync(inMs);
                var inputBytes = inMs.ToArray();

                var watermarkText = $"CONFIDENTIAL • {User.Identity?.Name ?? "user"} • {DateTime.UtcNow:u}";
                var watermarked = WatermarkImageSkia(inputBytes, watermarkText, ext, opacity: 0.22f, angleDeg: -25f);

                await _auditLogger.LogAsync(new AuditEntry(
                    DateTime.UtcNow, tenantId, RoomId, fileId, "Download", User.Identity?.Name,
                    originalName, watermarked.Length, null, Path.GetExtension(originalName)?.ToLowerInvariant(),
                    HttpContext.Connection.RemoteIpAddress?.ToString(),
                    Request.Headers.UserAgent.ToString().Truncate(256),
                    _auditLogger.NewCorrelationId(), null));

                var contentType = string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase)
                    ? "image/png"
                    : "image/jpeg";

                return File(new MemoryStream(watermarked), contentType, originalName);
            }
            else
            {
                // ... existing passthrough branch unchanged ...
                var props = await blob.GetPropertiesAsync();
                var contentType = string.IsNullOrWhiteSpace(props.Value.ContentType)
                    ? "application/octet-stream"
                    : props.Value.ContentType;

                var stream = await blob.OpenReadAsync();

                await _auditLogger.LogAsync(new AuditEntry(
                    DateTime.UtcNow, tenantId, RoomId, fileId, "Download", User.Identity?.Name,
                    originalName, metadata.Size, null, Path.GetExtension(originalName)?.ToLowerInvariant(),
                    HttpContext.Connection.RemoteIpAddress?.ToString(),
                    Request.Headers.UserAgent.ToString().Truncate(256),
                    _auditLogger.NewCorrelationId(), null));

                return File(stream, contentType, originalName);
            }
        }

        // ----- Helpers -----

        private static Guid ExtractFileId(string blobName)
        {
            var last = blobName.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrEmpty(last)) return Guid.Empty;
            var idx = last.IndexOf('_');
            if (idx <= 0) return Guid.Empty;
            return Guid.TryParse(last[..idx], out var g) ? g : Guid.Empty;
        }

        private static string NormalizeFolderPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                               .Select(SanitizeSegment)
                               .Where(s => !string.IsNullOrWhiteSpace(s))
                               .ToArray();
            if (segments.Length == 0) return string.Empty;
            return string.Join('/', segments) + "/";
        }

        private static string GetParentFolderPath(string path)
        {
            path = NormalizeFolderPath(path);
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count <= 1) return string.Empty;
            parts.RemoveAt(parts.Count - 1);
            return string.Join('/', parts) + "/";
        }

        private static string SanitizeSegment(string seg)
        {
            seg = seg.Trim();
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                seg = seg.Replace(c, '_');
            seg = seg.Replace("..", "_");
            if (seg.Length > 64) seg = seg[..64];
            return seg;
        }

        private const int MaxTagValueLength = 256;

        private static string EncodeFileNameForTag(string original)
        {
            if (string.IsNullOrEmpty(original)) return "";
            var trimmed = original.Trim();
            var encoded = Uri.EscapeDataString(trimmed);
            if (encoded.Length <= MaxTagValueLength) return encoded;

            var ext = System.IO.Path.GetExtension(trimmed);
            var baseName = System.IO.Path.GetFileNameWithoutExtension(trimmed);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed))).ToLowerInvariant();
            var shortBase = baseName.Length > 40 ? baseName[..40] : baseName;
            var composite = $"{shortBase}~{hash[..16]}{ext}";
            var finalEncoded = Uri.EscapeDataString(composite);
            return finalEncoded.Length <= MaxTagValueLength ? finalEncoded : finalEncoded[..MaxTagValueLength];
        }

        private static string TruncateForTag(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Trim();
            return value.Length <= MaxTagValueLength ? value : value[..MaxTagValueLength];
        }

        private static byte[] CreateWatermarkPngSkia(string text, int width, int height, float angleDeg, float opacity)
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            // Move to center and rotate
            canvas.Translate(width / 2f, height / 2f);
            canvas.RotateDegrees(angleDeg);

            using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(200, 0, 0, (byte)(opacity * 255)), // semi-transparent red
                TextSize = 72,
                Typeface = typeface,
                TextAlign = SKTextAlign.Center
            };

            // Centered text baseline adjustment
            var bounds = new SKRect();
            paint.MeasureText(text, ref bounds);
            var y = -(bounds.MidY); // center vertically

            canvas.DrawText(text, 0, y, paint);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        // NEW: burn watermark into PNG/JPEG using SkiaSharp
        private static byte[] WatermarkImageSkia(byte[] input, string text, string? ext, float opacity, float angleDeg)
        {
            using var bitmap = SKBitmap.Decode(input);
            var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;

            // draw original
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(bitmap, 0, 0);

            // overlay text
            canvas.Save();
            canvas.Translate(info.Width / 2f, info.Height / 2f);
            canvas.RotateDegrees(angleDeg);

            using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(200, 0, 0, (byte)(opacity * 255)),
                Typeface = typeface,
                TextAlign = SKTextAlign.Center
            };

            // scale text size relative to image
            paint.TextSize = Math.Min(info.Width, info.Height) / 6f;

            var bounds = new SKRect();
            paint.MeasureText(text, ref bounds);
            var y = -(bounds.MidY);
            canvas.DrawText(text, 0, y, paint);
            canvas.Restore();

            using var image = surface.Snapshot();
            var fmt = (ext?.Equals(".png", StringComparison.OrdinalIgnoreCase) == true)
                ? SKEncodedImageFormat.Png
                : SKEncodedImageFormat.Jpeg;
            var quality = (fmt == SKEncodedImageFormat.Jpeg) ? 90 : 100;

            using var data = image.Encode(fmt, quality);
            return data.ToArray();
        }
    }
}