using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Syncfusion.DocIO.DLS;
using twoSaaSCore.Models;
using twoSaaSCore.Services;
using WDocument = Syncfusion.DocIO.DLS.WordDocument;
using EJWordDocument = Syncfusion.EJ2.DocumentEditor.WordDocument;
using EJFormatType = Syncfusion.EJ2.DocumentEditor.FormatType;
using DocIOFormatType = Syncfusion.DocIO.FormatType;

namespace twoSaaSCore.Pages.Files
{
    [Authorize]
    public class ViewDocxModel : PageModel
    {
        private readonly ITenantProvider _tenantProvider;
        private readonly IRoomFileCatalog _catalog;
        private readonly IRoomPermissionService _permissions;
        private readonly IAuditLogger _auditLogger;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly DocumentConversionOptions _docOptions;

        public ViewDocxModel(
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

        /// <summary>SFDT JSON string consumed by the Syncfusion Document Editor.</summary>
        public string SfdtContent { get; private set; } = string.Empty;

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
            if (ext is not (".docx" or ".doc"))
                return BadRequest("Not a Word document.");

            FileName = originalName;

            var user = await _userManager.FindByIdAsync(userId);
            var userEmail = user?.Email;
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            WatermarkText = _docOptions.ResolveWatermark(userId, userEmail, tenantId.ToString(), ip);

            // Download the DOCX from blob, apply watermark, then convert to SFDT
            var sasUri = await _catalog.GetReadSasAsync(metadata.BlobName, TimeSpan.FromMinutes(3));
            using var httpClient = new System.Net.Http.HttpClient();
            await using var sourceStream = await httpClient.GetStreamAsync(sasUri);

            var ms = new MemoryStream();
            await sourceStream.CopyToAsync(ms);
            ms.Position = 0;

            var watermarked = ApplyDocxWatermark(ms, WatermarkText);
            watermarked.Position = 0;

            var sfdtDocument = EJWordDocument.Load(watermarked, GetImportFormatType(ext));
            SfdtContent = Newtonsoft.Json.JsonConvert.SerializeObject(sfdtDocument);
            sfdtDocument.Dispose();

            await _auditLogger.LogAsync(new AuditEntry(
                tenantId, RoomId, FileId, "View", userId, userEmail,
                FileName, metadata.Size, null,
                ext,
                ip,
                Request.Headers.UserAgent.ToString().Truncate(256),
                _auditLogger.NewCorrelationId(), null));

            return Page();
        }

        private static EJFormatType GetImportFormatType(string ext) =>
            ext switch
            {
                ".doc" => EJFormatType.Doc,
                _ => EJFormatType.Docx
            };

        /// <summary>
        /// Applies a diagonal text watermark and footer to a DOCX stream.
        /// Mirrors the logic in DownloadFileModel.
        /// </summary>
        private static MemoryStream ApplyDocxWatermark(Stream docxStream, string watermarkText)
        {
            using var document = new WDocument(docxStream, DocIOFormatType.Automatic);

            document.Watermark = new TextWatermark(watermarkText, "Calibri", 250, 40)
            {
                Color = Syncfusion.Drawing.Color.FromArgb(180, 180, 180),
                Semitransparent = true,
                Layout = WatermarkLayout.Diagonal
            };

            foreach (WSection section in document.Sections)
            {
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
            document.Save(output, DocIOFormatType.Docx);
            output.Position = 0;
            return output;
        }
    }
}
