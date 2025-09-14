using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using twoSaaSCore.Services;

namespace twoSaaSCore.Pages.Files
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly ITenantProvider _tenantProvider;
        private readonly IRoomFileCatalog _catalog;
        private readonly AzureBlobOptions _blobOptions;

        public IndexModel(ITenantProvider tenantProvider,
                          IRoomFileCatalog catalog,
                          IOptions<AzureBlobOptions> blobOptions)
        {
            _tenantProvider = tenantProvider;
            _catalog = catalog;
            _blobOptions = blobOptions.Value;
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

            folderPath = NormalizeFolderPath(folderPath);
            await _catalog.DeleteFileAsync(tenantId, roomId, fileId, folderPath);
            return RedirectToPage(new { roomId, folderPath });
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
    }
}