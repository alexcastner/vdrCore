namespace twoSaaSCore.Services;

/// <summary>
/// Configuration options for document watermarking and conversion.
/// Bound from the "DocumentConversion" section of appsettings.json.
/// </summary>
public class DocumentConversionOptions
{
    /// <summary>Whether watermarking is enabled for downloads and viewers.</summary>
    public bool WatermarkEnabled { get; set; } = true;

    /// <summary>
    /// Template string for watermark text. Supports placeholders:
    /// {UserId}, {Email}, {TenantId}, {Ip}, {Utc}.
    /// </summary>
    public string WatermarkText { get; set; } = "{Email} | {Ip} | {Utc}";

    /// <summary>Whether to cache server-side PDF conversions of Office files.</summary>
    public bool CachePdf { get; set; } = true;

    /// <summary>
    /// Resolves placeholder tokens in the watermark template to actual values.
    /// </summary>
    public string ResolveWatermark(string? userId, string? email, string? tenantId, string? ip)
    {
        if (!WatermarkEnabled || string.IsNullOrWhiteSpace(WatermarkText))
            return string.Empty;

        return WatermarkText
            .Replace("{UserId}", userId ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{Email}", email ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{TenantId}", tenantId ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{Ip}", ip ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{Utc}", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm"), StringComparison.OrdinalIgnoreCase);
    }
}
