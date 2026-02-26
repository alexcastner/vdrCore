using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace twoSaaSCore.Services
{
    public record RoomMetadata(Guid TenantId, Guid RoomId, string Name, string? NdaText,
                               DateTimeOffset CreatedUtc, string? CreatedByUserId);

    public record FileMetadata(Guid TenantId, Guid RoomId, Guid FileId, string OriginalFileName,
                               long Size, DateTimeOffset UploadedUtc, string? UploadedByUserId,
                               string BlobName, string? ContentType);

    public record VirtualFolderMetadata(Guid TenantId, Guid RoomId, Guid FolderId, string Path, string Name,
                                        DateTimeOffset CreatedUtc, string? CreatedByUserId);

    public interface IRoomFileCatalog
    {
        Task<RoomMetadata> CreateRoomAsync(Guid tenantId, string name, string? ndaText, string? userId, CancellationToken ct = default);
        Task<RoomMetadata?> GetRoomAsync(Guid tenantId, Guid roomId, CancellationToken ct = default);
        IAsyncEnumerable<RoomMetadata> ListRoomsAsync(Guid tenantId, CancellationToken ct = default);

        // Virtual folders
        Task<VirtualFolderMetadata> CreateVirtualFolderAsync(Guid tenantId, Guid roomId, string folderPath, string name, string? userId, CancellationToken ct = default);
        IAsyncEnumerable<VirtualFolderMetadata> ListVirtualFoldersAsync(Guid tenantId, Guid roomId, string? parentPath = null, CancellationToken ct = default);
        Task DeleteVirtualFolderAsync(Guid tenantId, Guid roomId, string folderPath, bool recursive, CancellationToken ct = default);

        // Files (folderPath optional)
        Task<FileMetadata> StoreFileAsync(Guid tenantId, Guid roomId, string originalFileName,
                                          Stream content, string? contentType, string? userId,
                                          string? folderPath = null, CancellationToken ct = default);

        Task<(string blobName, string sasUri)> GetWriteSasAsync(Guid tenantId, Guid roomId, string originalFileName,
                                                                TimeSpan lifetime, string? folderPath = null);

        Task<FileMetadata?> GetFileAsync(Guid tenantId, Guid roomId, Guid fileId, string? folderPath = null, CancellationToken ct = default);
        IAsyncEnumerable<FileMetadata> ListFilesAsync(Guid tenantId, Guid roomId, string? folderPath = null, CancellationToken ct = default);

        Task<string> GetReadSasAsync(string blobName, TimeSpan lifetime);
        Task DeleteFileAsync(Guid tenantId, Guid roomId, Guid fileId, string? folderPath = null, CancellationToken ct = default);

        Task DeleteRoomAsync(Guid tenantId, Guid roomId, CancellationToken ct = default);

        Task<RoomMetadata> CloneRoomAsync(Guid tenantId, Guid sourceRoomId, string newName, string? ndaText, string? userId, CancellationToken ct = default);
    }
}