using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Syncfusion.XlsIO;
using Syncfusion.EJ2.Spreadsheet;
using twoSaaSCore.Models;
using twoSaaSCore.Services;

namespace twoSaaSCore.Pages.Files
{
    [Authorize]
    public class ViewXlsxModel : PageModel
    {
        private readonly ITenantProvider _tenantProvider;
        private readonly IRoomFileCatalog _catalog;
        private readonly IRoomPermissionService _permissions;
        private readonly IAuditLogger _auditLogger;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly DocumentConversionOptions _docOptions;

        public ViewXlsxModel(
            ITenantProvider tenantProvider,
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

        [BindProperty(SupportsGet = true)]
        public Guid RoomId { get; set; }

        [BindProperty(SupportsGet = true)]
        public Guid FileId { get; set; }

        public string FileName { get; private set; } = string.Empty;
        public string WatermarkText { get; private set; } = string.Empty;

        /// <summary>URL the Spreadsheet component posts to for loading the workbook.</summary>
        public string OpenUrl { get; private set; } = string.Empty;

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
            var ext = Path.GetExtension(originalName).ToLowerInvariant();
            if (ext is not (".xlsx" or ".xls"))
                return BadRequest("Not an Excel file.");

            FileName = originalName;

            var user = await _userManager.FindByIdAsync(userId);
            var userEmail = user?.Email;
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            WatermarkText = _docOptions.ResolveWatermark(userId, userEmail, tenantId.ToString(), ip);

            OpenUrl = Url.Page("/Files/ViewXlsx", "Open", new { RoomId, FileId })!;

            await _auditLogger.LogAsync(new AuditEntry(
                tenantId, RoomId, FileId, "View", userId, userEmail,
                FileName, metadata.Size, null,
                ext, ip,
                Request.Headers.UserAgent.ToString().Truncate(256),
                _auditLogger.NewCorrelationId(), null));

            return Page();
        }

        /// <summary>
        /// POST handler invoked by the Syncfusion Spreadsheet open action.
        /// Downloads the Excel file from blob storage, applies the watermark,
        /// and returns the JSON the Spreadsheet component needs.
        /// </summary>
        public async Task<IActionResult> OnPostOpenAsync()
        {
            if (RoomId == Guid.Empty || FileId == Guid.Empty) return BadRequest();

            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            if (!await _permissions.HasPermissionAsync(tenantId, RoomId, userId, RoomPermission.ViewDocuments))
                return Forbid();

            var metadata = await _catalog.GetFileAsync(tenantId, RoomId, FileId);
            if (metadata == null) return NotFound();

            var user = await _userManager.FindByIdAsync(userId);
            var userEmail = user?.Email;
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var watermarkText = _docOptions.ResolveWatermark(userId, userEmail, tenantId.ToString(), ip);

            var sasUri = await _catalog.GetReadSasAsync(metadata.BlobName, TimeSpan.FromMinutes(3));
            using var httpClient = new System.Net.Http.HttpClient();
            await using var sourceStream = await httpClient.GetStreamAsync(sasUri);

            var ms = new MemoryStream();
            await sourceStream.CopyToAsync(ms);
            ms.Position = 0;

            var watermarked = ApplyXlsxWatermark(ms, watermarkText);
            watermarked.Position = 0;

            var openRequest = new OpenRequest();
            openRequest.File = new FormFile(watermarked, 0, watermarked.Length,
                "File", Uri.UnescapeDataString(metadata.OriginalFileName));

            var result = Workbook.Open(openRequest);
            return Content(result, "application/json");
        }

        /// <summary>
        /// Adds a watermark to every worksheet in an XLSX workbook.
        /// Mirrors the logic in DownloadFileModel.
        /// </summary>
        private static MemoryStream ApplyXlsxWatermark(Stream xlsxStream, string watermarkText)
        {
            using var engine = new ExcelEngine();
            var application = engine.Excel;
            application.DefaultVersion = ExcelVersion.Xlsx;

            var workbook = application.Workbooks.Open(xlsxStream);

            foreach (IWorksheet worksheet in workbook.Worksheets)
            {
                worksheet.PageSetup.CenterHeader = watermarkText;
                worksheet.PageSetup.LeftFooter = watermarkText;

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
    }
}
