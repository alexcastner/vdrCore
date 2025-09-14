using System;
using System.IO; // added
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using twoSaaSCore.Data;
using twoSaaSCore.Services;

namespace twoSaaSCore.Pages.Files
{
    [Authorize]
    public class DownloadModel : PageModel
    {
        private readonly ApplicationDbContext _db;
        private readonly IFileStorage _storage;

        public DownloadModel(ApplicationDbContext db, IFileStorage storage)
        {
            _db = db;
            _storage = storage;
        }

        private static bool CanInline(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext is ".pdf" or ".doc" or ".docx";
        }

        private static string ResolveContentType(string fileName, string? stored)
        {
            if (!string.IsNullOrWhiteSpace(stored))
                return stored;

            return Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".pdf" => "application/pdf",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                _ => "application/octet-stream"
            };
        }

        // Added optional 'view' parameter to request inline rendering
        public async Task<IActionResult> OnGetAsync(int id, bool? view)
        {
            var file = await _db.TenantFiles.FirstOrDefaultAsync(f => f.Id == id);
            if (file == null) return NotFound();

            var stream = await _storage.OpenReadAsync(file.BlobName);
            var contentType = ResolveContentType(file.FileName, file.ContentType);
            var inline = view == true && CanInline(file.FileName);

            if (inline)
            {
                Response.Headers["Content-Disposition"] = $"inline; filename=\"{file.FileName}\"";
                return File(stream, contentType);
            }

            return File(stream, contentType, file.FileName);
        }
    }
}