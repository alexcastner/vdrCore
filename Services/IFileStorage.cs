using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace twoSaaSCore.Services
{
    public interface IFileStorage
    {
        Task<string> UploadAsync(string blobName, Stream content, string? contentType, CancellationToken cancellationToken = default);
        Task DeleteAsync(string blobName, CancellationToken cancellationToken = default);
        string GetUri(string blobName);
        Task<Stream> OpenReadAsync(string blobName, CancellationToken cancellationToken = default);
        Task<(string blobName, string sasUri)> GenerateBlobWriteSasAsync(Guid tenantId, string originalFileName, TimeSpan lifetime);
        Task SetTagsAsync(string blobName, IDictionary<string, string> tags, CancellationToken cancellationToken = default);
        Task<IDictionary<string, string>> GetTagsAsync(string blobName, CancellationToken cancellationToken = default);
        Task<string?> FindBlobByMd5Async(Guid tenantId, string md5Base64, CancellationToken cancellationToken = default);
        Task<string> GenerateBlobReadSasAsync(string blobName, TimeSpan lifetime);
    }
}