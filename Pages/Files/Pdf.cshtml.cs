using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using twoSaaSCore.Data;
using twoSaaSCore.Services.Files;

namespace twoSaaSCore.Pages.Files;

[Authorize]
public class PdfModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly IDocToPdfConverter _converter;

    public PdfModel(ApplicationDbContext db, IDocToPdfConverter converter)
    {
        _db = db;
        _converter = converter;
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var tenantIdStr = User.FindFirstValue("tenantId");
        if (!Guid.TryParse(tenantIdStr, out var tenantId)) return Forbid();

        var file = await _db.TenantFiles.FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId);
        if (file == null) return NotFound();

        // If it's already a PDF just stream it.
        if (file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var pdfPathDirect = Path.Combine(AppContext.BaseDirectory, "data", "tenants", tenantId.ToString(), file.BlobName);
            if (!System.IO.File.Exists(pdfPathDirect)) return NotFound();
            return PhysicalFile(pdfPathDirect, "application/pdf");
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
        var (pdfPath, _) = await _converter.EnsurePdfAsync(file, userId);

        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(pdfPath, "application/pdf");
    }
}