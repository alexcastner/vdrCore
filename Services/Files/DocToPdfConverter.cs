using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
using Syncfusion.DocIORenderer;
using Syncfusion.Licensing;
using twoSaaSCore.Data;
using twoSaaSCore.Models;

namespace twoSaaSCore.Services.Files;

public sealed class DocToPdfConverter : IDocToPdfConverter
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();
    private readonly ApplicationDbContext _db;
    private readonly ILogger<DocToPdfConverter> _logger;
    private readonly IConfiguration _cfg;

    private readonly bool _watermarkEnabled;
    private readonly string _watermarkTemplate;
    private readonly bool _cachePdf;

    public DocToPdfConverter(ApplicationDbContext db, ILogger<DocToPdfConverter> logger, IConfiguration cfg)
    {
        _db = db;
        _logger = logger;
        _cfg = cfg;
        _watermarkEnabled = cfg.GetValue("DocumentConversion:WatermarkEnabled", true);
        _watermarkTemplate = cfg.GetValue<string>("DocumentConversion:WatermarkText") 
                             ?? "{UserId} | {TenantId} | {Utc}";
        _cachePdf = cfg.GetValue("DocumentConversion:CachePdf", true);
    }

    public async Task<(string physicalPdfPath, string relativeBlobName)> EnsurePdfAsync(TenantFile sourceFile, string userId)
    {
        if (!sourceFile.FileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) &&
            !sourceFile.FileName.EndsWith(".doc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source is not a Word document.");

        var pdfBlobName = Regex.Replace(sourceFile.BlobName, @"\.(docx|doc)$", ".pdf", RegexOptions.IgnoreCase);
        var tenantRoot = Path.Combine(AppContext.BaseDirectory, "data", "tenants", sourceFile.TenantId.ToString());
        var sourcePath = Path.Combine(tenantRoot, sourceFile.BlobName);
        var pdfPath = Path.Combine(tenantRoot, pdfBlobName);

        if (_cachePdf && System.IO.File.Exists(pdfPath))
            return (pdfPath, pdfBlobName);

        var gate = _locks.GetOrAdd(sourceFile.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (_cachePdf && System.IO.File.Exists(pdfPath))
                return (pdfPath, pdfBlobName);

            if (!System.IO.File.Exists(sourcePath))
                throw new FileNotFoundException("Source DOC/DOCX missing.", sourcePath);

            Directory.CreateDirectory(tenantRoot);

            await using var fs = System.IO.File.OpenRead(sourcePath);
            using var word = new WordDocument(fs, GetFormat(sourceFile.FileName));

            if (_watermarkEnabled)
                InsertDiagonalWatermark(word, ExpandWatermark(_watermarkTemplate, sourceFile, userId));

            using var renderer = new DocIORenderer();
            using var pdfDoc = renderer.ConvertToPDF(word);
            await using var outStream = System.IO.File.Create(pdfPath);
            pdfDoc.Save(outStream);
            pdfDoc.Close(true);

            // (Optional) update a metadata column if you add one (e.g., ConvertedPdfBlobName)
            // sourceFile.ConvertedPdfBlobName = pdfBlobName; await _db.SaveChangesAsync();

            return (pdfPath, pdfBlobName);
        }
        finally
        {
            gate.Release();
        }
    }

    private static FormatType GetFormat(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".docx" => FormatType.Docx,
            ".doc" => FormatType.Doc,
            _ => throw new NotSupportedException("Unsupported Word format.")
        };

    private static void InsertDiagonalWatermark(WordDocument doc, string text)
    {
        // Place watermark shape in header (first section)
        foreach (WSection section in doc.Sections)
        {
            var header = section.HeadersFooters.Header;
            var paragraph = header.AddParagraph();
            var shape = paragraph.AppendShape(AutoShapeType.TextPlainText, 500, 100);
            shape.WrapFormat.TextWrappingStyle = TextWrappingStyle.Behind;
            shape.HorizontalPosition = 100;
            shape.VerticalPosition = 120;
            shape.Rotation = -40;
            shape.TextFrame.TextRange.Text = text;
            shape.TextFrame.TextRange.CharacterFormat.FontName = "Arial";
            shape.TextFrame.TextRange.CharacterFormat.FontSize = 36;
            shape.TextFrame.TextRange.CharacterFormat.TextColor = Syncfusion.Drawing.Color.FromArgb(160, 160, 160);
            shape.TextFrame.TextRange.CharacterFormat.Bold = true;
            shape.FillFormat.Color = Syncfusion.Drawing.Color.White;
            shape.LineFormat.Color = Syncfusion.Drawing.Color.White;
        }
    }

    private string ExpandWatermark(string template, TenantFile f, string userId)
    {
        return template
            .Replace("{UserId}", userId, StringComparison.Ordinal)
            .Replace("{TenantId}", f.TenantId.ToString(), StringComparison.Ordinal)
            .Replace("{FileId}", f.Id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{Utc}", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}