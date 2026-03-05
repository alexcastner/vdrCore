namespace twoSaaSCore.Services;

/// <summary>Options bound to the "DocumentConversion" configuration section.</summary>
public class DocumentConversionOptions
{
    /// <summary>Whether to apply a watermark to downloaded/viewed PDFs.</summary>
    public bool WatermarkEnabled { get; set; } = true;

    /// <summary>
    /// Template for the watermark text stamped on each PDF page.
    /// Supported placeholders: {UserId}, {Email}, {TenantId}, {Ip}, {Utc}.
    /// </summary>
    public string WatermarkText { get; set; } = "{Email} | {Ip} | {Utc}";

    /// <summary>Whether to cache Office-to-PDF conversions in blob storage.</summary>
    public bool CachePdf { get; set; } = true;

    /// <summary>
    /// Resolves placeholder tokens in <see cref="WatermarkText"/> with runtime values.
    /// </summary>
    public string ResolveWatermark(string? userId = null, string? email = null,
                                    string? tenantId = null, string? ip = null)
    {
        return WatermarkText
            .Replace("{UserId}", userId ?? "", System.StringComparison.OrdinalIgnoreCase)
            .Replace("{Email}", email ?? "", System.StringComparison.OrdinalIgnoreCase)
            .Replace("{TenantId}", tenantId ?? "", System.StringComparison.OrdinalIgnoreCase)
            .Replace("{Ip}", ip ?? "", System.StringComparison.OrdinalIgnoreCase)
            .Replace("{Utc}", System.DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC",
                     System.StringComparison.OrdinalIgnoreCase);
    }
}
