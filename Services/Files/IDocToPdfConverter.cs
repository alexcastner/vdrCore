using System.Threading.Tasks;
using twoSaaSCore.Models;

namespace twoSaaSCore.Services.Files;

public interface IDocToPdfConverter
{
    Task<(string physicalPdfPath, string relativeBlobName)> EnsurePdfAsync(TenantFile sourceFile, string userId);
}