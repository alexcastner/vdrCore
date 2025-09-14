using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace twoSaaSCore.Services
{
    public class AzureBlobOptions
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string Container { get; set; } = "files";
        public bool CreateContainerIfNotExists { get; set; } = true;
    }

    public class AzureBlobFileStorage : IFileStorage
    {
        private readonly BlobContainerClient _container;
        private readonly AzureBlobOptions _options;
        private readonly BlobServiceClient _service;

        public AzureBlobFileStorage(IOptions<AzureBlobOptions> options)
        {
            _options = options.Value;
            _service = new BlobServiceClient(_options.ConnectionString);
            _container = _service.GetBlobContainerClient(_options.Container);
            if (_options.CreateContainerIfNotExists)
            {
                _container.CreateIfNotExists(PublicAccessType.None);
            }
        }

        public async Task<string> UploadAsync(string blobName, Stream content, string? contentType, CancellationToken cancellationToken = default)
        {
            var blob = _container.GetBlobClient(blobName);
            var headers = new BlobHttpHeaders();
            if (!string.IsNullOrWhiteSpace(contentType)) headers.ContentType = contentType;
            await blob.UploadAsync(content, new BlobUploadOptions { HttpHeaders = headers }, cancellationToken);
            return blob.Uri.ToString();
        }

        public async Task DeleteAsync(string blobName, CancellationToken cancellationToken = default)
        {
            var blob = _container.GetBlobClient(blobName);
            await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, conditions: null, cancellationToken);
        }

        public string GetUri(string blobName) => _container.GetBlobClient(blobName).Uri.ToString();

        public async Task<Stream> OpenReadAsync(string blobName, CancellationToken cancellationToken = default)
        {
            var blob = _container.GetBlobClient(blobName);
            var response = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
            return response.Value.Content;
        }

        public async Task<(string blobName, string sasUri)> GenerateBlobWriteSasAsync(Guid tenantId, string originalFileName, TimeSpan lifetime)
        {
            await EnsureTenantPrefixExistsAsync(tenantId);
            var safeName = Path.GetFileName(originalFileName);
            var blobName = $"{tenantId}/{Guid.NewGuid()}_{safeName}";
            var blob = _container.GetBlobClient(blobName);

            var builder = new BlobSasBuilder
            {
                BlobContainerName = _container.Name,
                BlobName = blobName,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.Add(lifetime)
            };
            builder.SetPermissions(BlobSasPermissions.Write | BlobSasPermissions.Create);
            var sasUri = blob.GenerateSasUri(builder).ToString();
            return (blobName, sasUri);
        }

        public async Task SetTagsAsync(string blobName, IDictionary<string, string> tags, CancellationToken cancellationToken = default)
        {
            var blob = _container.GetBlobClient(blobName);
            await blob.SetTagsAsync(tags, cancellationToken: cancellationToken);
        }

        public async Task<IDictionary<string, string>> GetTagsAsync(string blobName, CancellationToken cancellationToken = default)
        {
            var blob = _container.GetBlobClient(blobName);
            var resp = await blob.GetTagsAsync(cancellationToken: cancellationToken);
            return resp.Value.Tags;
        }

        public async Task<string?> FindBlobByMd5Async(Guid tenantId, string md5Base64, CancellationToken cancellationToken = default)
        {
            var expr = $"@container='{_container.Name}' AND md5='{md5Base64}' AND tenantId='{tenantId}'";
            await foreach (var item in _service.FindBlobsByTagsAsync(expr, cancellationToken: cancellationToken))
            {
                return item.BlobName;
            }
            return null;
        }

        public Task<string> GenerateBlobReadSasAsync(string blobName, TimeSpan lifetime)
        {
            var blob = _container.GetBlobClient(blobName);
            var builder = new BlobSasBuilder
            {
                BlobContainerName = _container.Name,
                BlobName = blobName,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.Add(lifetime)
            };
            builder.SetPermissions(BlobSasPermissions.Read);
            var sasUri = blob.GenerateSasUri(builder).ToString();
            return Task.FromResult(sasUri);
        }

        private async Task EnsureTenantPrefixExistsAsync(Guid tenantId)
        {
            var placeholderName = $"{tenantId}/.init";
            var placeholder = _container.GetBlobClient(placeholderName);
            if (!await placeholder.ExistsAsync())
            {
                using var empty = new MemoryStream(Array.Empty<byte>());
                try
                {
                    await placeholder.UploadAsync(empty, new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders { ContentType = "application/octet-stream" },
                        Metadata = { ["tenant"] = tenantId.ToString() }
                    });
                }
                catch (Azure.RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobAlreadyExists) { }
            }
        }
    }
}