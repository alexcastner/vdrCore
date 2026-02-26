using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using twoSaaSCore.Data;
using twoSaaSCore.Models;

namespace twoSaaSCore.Services
{
    public class BlobRoomFileCatalog : IRoomFileCatalog
    {
        private readonly BlobContainerClient _container;
        private readonly BlobServiceClient _service;
        private readonly AzureBlobOptions _options;
        private readonly ApplicationDbContext _db;

        private const string FolderMarkerName = "_folder.json";
        private const string RoomMarkerName = "_room.json";
        private const int MaxTagValueLength = 256;
        private const string TagFileNameEncoded = "fileNameEnc";
        private const string TagFolder = "isFolder";

        public BlobRoomFileCatalog(IOptions<AzureBlobOptions> opt, ApplicationDbContext db)
        {
            _options = opt.Value;
            _service = new BlobServiceClient(_options.ConnectionString);
            _container = _service.GetBlobContainerClient(_options.Container);
            _db = db;
            if (_options.CreateContainerIfNotExists)
                _container.CreateIfNotExists(PublicAccessType.None);
        }

        private string RoomMetaPath(Guid tenantId, Guid roomId) => $"{tenantId}/{roomId}/{RoomMarkerName}";
        private string FolderMetaPath(Guid tenantId, Guid roomId, string folderPath)
            => $"{tenantId}/{roomId}/{NormalizeFolderPath(folderPath)}{FolderMarkerName}";

        private static string FileBlobName(Guid tenantId, Guid roomId, string originalName, string? folderPath)
        {
            var prefix = $"{tenantId}/{roomId}/";
            if (!string.IsNullOrWhiteSpace(folderPath))
                prefix += NormalizeFolderPath(folderPath);
            return $"{prefix}{Guid.NewGuid()}_{Sanitize(originalName)}";
        }

        // ---------------- Rooms ----------------
        public async Task<RoomMetadata> CreateRoomAsync(Guid tenantId, string name, string? ndaText, string? userId, CancellationToken ct = default)
        {
            var roomId = Guid.NewGuid();
            var meta = new RoomMetadata(tenantId, roomId, name, ndaText, DateTimeOffset.UtcNow, userId);
            var blob = _container.GetBlobClient(RoomMetaPath(tenantId, roomId));
            using var ms = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(meta));
            await blob.UploadAsync(ms, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            }, ct);
            await blob.SetTagsAsync(new Dictionary<string, string>
            {
                {"tenantId", tenantId.ToString()},
                {"roomId", roomId.ToString()},
                {"name", TruncateForTag(name)}
            }, cancellationToken: ct);
            return meta;
        }

        public async Task<RoomMetadata?> GetRoomAsync(Guid tenantId, Guid roomId, CancellationToken ct = default)
        {
            var blob = _container.GetBlobClient(RoomMetaPath(tenantId, roomId));
            if (!await blob.ExistsAsync(ct)) return null;
            var dl = await blob.DownloadContentAsync(ct);
            return JsonSerializer.Deserialize<RoomMetadata>(dl.Value.Content.ToStream());
        }

        public async IAsyncEnumerable<RoomMetadata> ListRoomsAsync(Guid tenantId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var prefix = $"{tenantId}/";
            await foreach (var item in _container.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
            {
                if (!item.Name.EndsWith($"/{RoomMarkerName}", StringComparison.OrdinalIgnoreCase))
                    continue;
                var blob = _container.GetBlobClient(item.Name);
                var dl = await blob.DownloadContentAsync(ct);
                var meta = JsonSerializer.Deserialize<RoomMetadata>(dl.Value.Content.ToStream());
                if (meta != null) yield return meta;
            }
        }

        // ---------------- Virtual Folders ----------------
        public async Task<VirtualFolderMetadata> CreateVirtualFolderAsync(Guid tenantId, Guid roomId, string folderPath, string name, string? userId, CancellationToken ct = default)
        {
            folderPath = NormalizeFolderPath(folderPath); // ensures trailing slash
            if (string.IsNullOrWhiteSpace(folderPath))
                throw new ArgumentException("folderPath required");

            var folderId = Guid.NewGuid();
            var meta = new VirtualFolderMetadata(tenantId, roomId, folderId, folderPath, name, DateTimeOffset.UtcNow, userId);
            var metaBlob = _container.GetBlobClient(FolderMetaPath(tenantId, roomId, folderPath));

            using var ms = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(meta));
            await metaBlob.UploadAsync(ms, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            }, ct);


            await metaBlob.SetTagsAsync(new Dictionary<string, string>
            {
                {"tenantId", tenantId.ToString()},
                {"roomId", roomId.ToString()},
                {"folderId", folderId.ToString()},
                {TagFolder, "1"},
                {"path", folderPath},
                {"name", TruncateForTag(name)}
            }, cancellationToken: ct);

            return meta;
        }

        public async IAsyncEnumerable<VirtualFolderMetadata> ListVirtualFoldersAsync(Guid tenantId, Guid roomId, string? parentPath = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            parentPath = NormalizeFolderPath(parentPath);
            var prefix = $"{tenantId}/{roomId}/";
            if (!string.IsNullOrEmpty(parentPath))
                prefix += parentPath;

            await foreach (var item in _container.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
            {
                if (!item.Name.EndsWith($"/{FolderMarkerName}", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Filter only immediate children if parentPath specified
                if (!IsImmediateChildFolder(prefix, item.Name))
                    continue;

                var blob = _container.GetBlobClient(item.Name);
                var dl = await blob.DownloadContentAsync(ct);
                var meta = JsonSerializer.Deserialize<VirtualFolderMetadata>(dl.Value.Content.ToStream());
                if (meta != null) yield return meta;
            }
        }

        public async Task DeleteVirtualFolderAsync(Guid tenantId, Guid roomId, string folderPath, bool recursive, CancellationToken ct = default)
        {
            folderPath = NormalizeFolderPath(folderPath);
            if (string.IsNullOrEmpty(folderPath)) return;
            var prefix = $"{tenantId}/{roomId}/{folderPath}";
            if (!recursive)
            {
                // Only delete if no child blobs (excluding its own folder marker)
                await foreach (var blobItem in _container.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
                {
                    if (blobItem.Name.EndsWith($"/{FolderMarkerName}", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Found content ? abort
                    throw new InvalidOperationException("Folder not empty; use recursive");
                }
            }

            await foreach (var blobItem in _container.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
            {
                await _container.DeleteBlobIfExistsAsync(blobItem.Name, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
            }
        }

        // ---------------- Files ----------------
        public async Task<FileMetadata> StoreFileAsync(Guid tenantId, Guid roomId, string originalFileName,
                                                       Stream content, string? contentType, string? userId,
                                                       string? folderPath = null, CancellationToken ct = default)
        {
            folderPath = NormalizeFolderPath(folderPath);
            var blobName = FileBlobName(tenantId, roomId, originalFileName, folderPath);
            var fileId = ExtractFileId(blobName);
            var blob = _container.GetBlobClient(blobName);

            long size = 0;
            using (var measuring = new MeasuringStream(content, c => size += c))
            {
                await blob.UploadAsync(measuring, new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = contentType ?? "application/octet-stream" },
                    Tags = new Dictionary<string, string>
                    {
                        {"tenantId", tenantId.ToString()},
                        {"roomId", roomId.ToString()},
                        {"fileId", fileId.ToString()},
                        {TagFileNameEncoded, EncodeFileNameForTag(originalFileName)},
                        {"uploadedBy", TruncateForTag(userId ?? "")},
                        {"uploadedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()},
                        {"folderPath", folderPath ?? ""}
                    }
                }, ct);
            }

            // Write SQL reference
            _db.RoomFileRefs.Add(new RoomFileRef
            {
                TenantId = tenantId,
                RoomId = roomId,
                FileId = fileId,
                BlobName = blobName,
                OriginalFileName = originalFileName,
                Size = size,
                ContentType = contentType,
                FolderPath = string.IsNullOrEmpty(folderPath) ? null : folderPath,
                AddedUtc = DateTimeOffset.UtcNow,
                AddedByUserId = userId
            });
            await _db.SaveChangesAsync(ct);

            return new FileMetadata(tenantId, roomId, fileId, originalFileName, size, DateTimeOffset.UtcNow, userId, blobName, contentType);
        }

        public async Task<(string blobName, string sasUri)> GetWriteSasAsync(Guid tenantId, Guid roomId, string originalFileName, TimeSpan lifetime, string? folderPath = null)
        {
            folderPath = NormalizeFolderPath(folderPath);
            var blobName = FileBlobName(tenantId, roomId, originalFileName, folderPath);
            var blob = _container.GetBlobClient(blobName);
            var builder = new BlobSasBuilder
            {
                BlobContainerName = _container.Name,
                BlobName = blobName,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.Add(lifetime)
            };
            builder.SetPermissions(BlobSasPermissions.Write | BlobSasPermissions.Create);
            return (blobName, blob.GenerateSasUri(builder).ToString());
        }

        public async Task<FileMetadata?> GetFileAsync(Guid tenantId, Guid roomId, Guid fileId, string? folderPath = null, CancellationToken ct = default)
        {
            var ref_ = await _db.RoomFileRefs
                .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.RoomId == roomId && r.FileId == fileId, ct);

            if (ref_ != null)
                return new FileMetadata(tenantId, roomId, ref_.FileId, ref_.OriginalFileName,
                    ref_.Size, ref_.AddedUtc, ref_.AddedByUserId, ref_.BlobName, ref_.ContentType);

            // Fallback: scan blobs for legacy files not yet in SQL
            folderPath = NormalizeFolderPath(folderPath);
            var prefix = $"{tenantId}/{roomId}/";
            if (!string.IsNullOrEmpty(folderPath))
                prefix += folderPath;

            await foreach (var item in _container.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
            {
                if (item.Name.EndsWith($"/{RoomMarkerName}") || item.Name.EndsWith($"/{FolderMarkerName}"))
                    continue;
                if (ExtractFileId(item.Name) == fileId)
                {
                    var blob = _container.GetBlobClient(item.Name);
                    var tags = await blob.GetTagsAsync(cancellationToken: ct);
                    var origName = DecodeFileNameFromTags(tags.Value.Tags) ?? StripLeadingGuid(item.Name);
                    var uploadedAt = tags.Value.Tags.TryGetValue("uploadedAt", out var tsStr) && long.TryParse(tsStr, out var ts)
                        ? DateTimeOffset.FromUnixTimeSeconds(ts) : DateTimeOffset.UtcNow;
                    var uploadedBy = tags.Value.Tags.TryGetValue("uploadedBy", out var ub) ? ub : null;
                    return new FileMetadata(tenantId, roomId, fileId, origName,
                        item.Properties.ContentLength ?? 0, uploadedAt, uploadedBy, item.Name, item.Properties.ContentType);
                }
            }
            return null;
        }

        public async IAsyncEnumerable<FileMetadata> ListFilesAsync(Guid tenantId, Guid roomId, string? folderPath = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            folderPath = NormalizeFolderPath(folderPath);
            var normalizedFolder = string.IsNullOrEmpty(folderPath) ? (string?)null : folderPath;

            var refs = await _db.RoomFileRefs
                .Where(r => r.TenantId == tenantId && r.RoomId == roomId && r.FolderPath == normalizedFolder)
                .OrderByDescending(r => r.AddedUtc)
                .ToListAsync(ct);

            if (refs.Count > 0)
            {
                foreach (var r in refs)
                    yield return new FileMetadata(tenantId, roomId, r.FileId, r.OriginalFileName,
                        r.Size, r.AddedUtc, r.AddedByUserId, r.BlobName, r.ContentType);
                yield break;
            }

            // Fallback: scan blobs for legacy files not yet in SQL
            var prefix = $"{tenantId}/{roomId}/";
            if (!string.IsNullOrEmpty(folderPath))
                prefix += folderPath;

            await foreach (var item in _container.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
            {
                if (item.Name.EndsWith($"/{RoomMarkerName}") || item.Name.EndsWith($"/{FolderMarkerName}"))
                    continue;
                var fileId = ExtractFileId(item.Name);
                if (fileId == Guid.Empty) continue;

                var blob = _container.GetBlobClient(item.Name);
                var tags = await blob.GetTagsAsync(cancellationToken: ct);
                var origName = DecodeFileNameFromTags(tags.Value.Tags) ?? StripLeadingGuid(item.Name);
                var uploadedAt = tags.Value.Tags.TryGetValue("uploadedAt", out var tsStr) && long.TryParse(tsStr, out var ts)
                    ? DateTimeOffset.FromUnixTimeSeconds(ts) : DateTimeOffset.UtcNow;
                var uploadedBy = tags.Value.Tags.TryGetValue("uploadedBy", out var ub) ? ub : null;

                yield return new FileMetadata(tenantId, roomId, fileId, origName,
                    item.Properties.ContentLength ?? 0, uploadedAt, uploadedBy, item.Name, item.Properties.ContentType);
            }
        }

        public Task<string> GetReadSasAsync(string blobName, TimeSpan lifetime)
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
            return Task.FromResult(blob.GenerateSasUri(builder).ToString());
        }

        public async Task DeleteFileAsync(Guid tenantId, Guid roomId, Guid fileId, string? folderPath = null, CancellationToken ct = default)
        {
            var ref_ = await _db.RoomFileRefs
                .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.RoomId == roomId && r.FileId == fileId, ct);

            if (ref_ != null)
            {
                var blobName = ref_.BlobName;
                _db.RoomFileRefs.Remove(ref_);
                await _db.SaveChangesAsync(ct);

                // Only delete the physical blob if no other rooms reference it
                var otherRefs = await _db.RoomFileRefs
                    .IgnoreQueryFilters()
                    .AnyAsync(r => r.BlobName == blobName, ct);
                if (!otherRefs)
                    await _container.DeleteBlobIfExistsAsync(blobName, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
            }
            else
            {
                // Fallback for legacy files
                var meta = await GetFileAsync(tenantId, roomId, fileId, folderPath, ct);
                if (meta != null)
                    await _container.DeleteBlobIfExistsAsync(meta.BlobName, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
            }
        }

        public async Task DeleteRoomAsync(Guid tenantId, Guid roomId, CancellationToken ct = default)
        {
            // Remove file refs; only delete blobs with no remaining refs
            var refs = await _db.RoomFileRefs
                .Where(r => r.TenantId == tenantId && r.RoomId == roomId)
                .ToListAsync(ct);

            var blobNames = refs.Select(r => r.BlobName).Distinct().ToList();
            _db.RoomFileRefs.RemoveRange(refs);
            await _db.SaveChangesAsync(ct);

            foreach (var blobName in blobNames)
            {
                var stillReferenced = await _db.RoomFileRefs
                    .IgnoreQueryFilters()
                    .AnyAsync(r => r.BlobName == blobName, ct);
                if (!stillReferenced)
                    await _container.DeleteBlobIfExistsAsync(blobName, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
            }

            // Delete folder markers and room marker blobs
            var prefix = $"{tenantId}/{roomId}/";
            await foreach (var blob in _container.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
            {
                await _container.DeleteBlobIfExistsAsync(blob.Name, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
            }
        }

        public async Task<RoomMetadata> CloneRoomAsync(Guid tenantId, Guid sourceRoomId, string newName, string? ndaText, string? userId, CancellationToken ct = default)
        {
            // Create the new room
            var newRoom = await CreateRoomAsync(tenantId, newName, ndaText, userId, ct);
            var newRoomId = newRoom.RoomId;

            // Clone folder markers (lightweight blob copies for folder structure)
            var sourcePrefix = $"{tenantId}/{sourceRoomId}/";
            var destPrefix = $"{tenantId}/{newRoomId}/";

            await foreach (var item in _container.GetBlobsAsync(prefix: sourcePrefix, cancellationToken: ct))
            {
                if (!item.Name.EndsWith($"/{FolderMarkerName}", StringComparison.OrdinalIgnoreCase))
                    continue;

                var relativePath = item.Name[sourcePrefix.Length..];
                var sourceBlob = _container.GetBlobClient(item.Name);
                var destBlob = _container.GetBlobClient(destPrefix + relativePath);
                var copyOp = await destBlob.StartCopyFromUriAsync(sourceBlob.Uri, cancellationToken: ct);
                await copyOp.WaitForCompletionAsync(ct);

                try
                {
                    var srcTags = await sourceBlob.GetTagsAsync(cancellationToken: ct);
                    var newTags = new Dictionary<string, string>(srcTags.Value.Tags)
                    {
                        ["roomId"] = newRoomId.ToString(),
                        ["folderId"] = Guid.NewGuid().ToString()
                    };
                    await destBlob.SetTagsAsync(newTags, cancellationToken: ct);
                }
                catch { /* tags are best-effort */ }
            }

            // Clone file references — just INSERT new rows pointing to the SAME blobs
            var sourceRefs = await _db.RoomFileRefs
                .Where(r => r.TenantId == tenantId && r.RoomId == sourceRoomId)
                .ToListAsync(ct);

            foreach (var src in sourceRefs)
            {
                _db.RoomFileRefs.Add(new RoomFileRef
                {
                    TenantId = tenantId,
                    RoomId = newRoomId,
                    FileId = Guid.NewGuid(),
                    BlobName = src.BlobName, // same physical blob!
                    OriginalFileName = src.OriginalFileName,
                    Size = src.Size,
                    ContentType = src.ContentType,
                    FolderPath = src.FolderPath,
                    AddedUtc = DateTimeOffset.UtcNow,
                    AddedByUserId = userId
                });
            }
            await _db.SaveChangesAsync(ct);

            return newRoom;
        }

        // ------------- Helpers -------------
        private static Guid ExtractFileId(string blobName)
        {
            var last = blobName.Split('/').Last();
            var underscore = last.IndexOf('_');
            if (underscore <= 0) return Guid.Empty;
            return Guid.TryParse(last[..underscore], out var g) ? g : Guid.Empty;
        }

        private static string StripLeadingGuid(string blobName)
        {
            var last = blobName.Split('/').Last();
            var underscore = last.IndexOf('_');
            return underscore > 0 ? last[(underscore + 1)..] : last;
        }

        private static string NormalizeFolderPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            var segments = path
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(SanitizeSegment)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            if (segments.Length == 0) return string.Empty;
            return string.Join('/', segments) + "/";
        }

        private static bool IsImmediateChildFolder(string parentPrefix, string fullPath)
        {
            // parentPrefix ends with '/', fullPath includes marker _folder.json
            // Example: parentPrefix = t/r/a/ ; fullPath = t/r/a/b/_folder.json -> immediate
            var withoutParent = fullPath.Substring(parentPrefix.Length);
            return withoutParent.Count(c => c == '/') == 1; // 'b/_folder.json'
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "file";
            var cleaned = name.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
                cleaned = cleaned.Replace(c, '_');
            if (cleaned.Length > 180)
            {
                var ext = Path.GetExtension(cleaned);
                var basePart = cleaned[..Math.Max(1, 180 - ext.Length)];
                cleaned = basePart + ext;
            }
            return cleaned;
        }

        private static string SanitizeSegment(string seg)
        {
            seg = seg.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
                seg = seg.Replace(c, '_');
            seg = seg.Replace("..", "_");
            if (seg.Length > 64) seg = seg[..64];
            return seg;
        }

        private static string EncodeFileNameForTag(string original)
        {
            if (string.IsNullOrEmpty(original)) return "";
            var trimmed = original.Trim();
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(trimmed));
            if (encoded.Length <= MaxTagValueLength) return encoded;

            var ext = Path.GetExtension(trimmed);
            var baseName = Path.GetFileNameWithoutExtension(trimmed);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed))).ToLowerInvariant();
            var shortBase = baseName.Length > 40 ? baseName[..40] : baseName;
            var composite = $"{shortBase}~{hash[..16]}{ext}";
            var finalEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(composite));
            return finalEncoded.Length <= MaxTagValueLength ? finalEncoded : finalEncoded[..MaxTagValueLength];
        }

        private static string? DecodeFileNameFromTags(IDictionary<string, string> tags)
        {
            if (!tags.TryGetValue(TagFileNameEncoded, out var enc) || string.IsNullOrEmpty(enc)) return null;
            try
            {
                var bytes = Convert.FromBase64String(enc);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                // Fall back to URI decoding for legacy tags
                try { return Uri.UnescapeDataString(enc); } catch { return enc; }
            }
        }

        private static string SanitizeForTag(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (char.IsLetterOrDigit(c) || c is ' ' or '+' or '-' or '.' or ':' or '=' or '_' or '/')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            return sb.ToString();
        }

        private static string TruncateForTag(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sanitized = SanitizeForTag(value.Trim());
            return sanitized.Length <= MaxTagValueLength ? sanitized : sanitized[..MaxTagValueLength];
        }

        private sealed class MeasuringStream : Stream
        {
            private readonly Stream _inner;
            private readonly Action<long> _on;
            public MeasuringStream(Stream inner, Action<long> on) { _inner = inner; _on = on; }
            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count)
            {
                var r = _inner.Read(buffer, offset, count);
                _on(r);
                return r;
            }
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => base.ReadAsync(buffer, cancellationToken);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}