using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using twoSaaSCore.Services;

namespace twoSaaSCore.Pages.Files
{
    [Authorize]
    public class ViewPdfModel : PageModel
    {
        private readonly ITenantProvider _tenantProvider;
        private readonly IRoomFileCatalog _catalog;

        public ViewPdfModel(ITenantProvider tenantProvider, IRoomFileCatalog catalog)
        {
            _tenantProvider = tenantProvider;
            _catalog = catalog;
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