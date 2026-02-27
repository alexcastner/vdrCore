using System;
using System.ComponentModel.DataAnnotations;

namespace twoSaaSCore.Models
{
    public class RoomFileRef : ITenantEntity
    {
        public int Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid RoomId { get; set; }
        public Guid FileId { get; set; }
        public string BlobName { get; set; } = default!;
        public string OriginalFileName { get; set; } = default!;
        public long Size { get; set; }
        public string? ContentType { get; set; }
        public string? FolderPath { get; set; }
        public DateTimeOffset AddedUtc { get; set; }
        public string? AddedByUserId { get; set; }

        /// <summary>Azure AI Foundry file ID (set when file is uploaded to the room's vector store).</summary>
        [MaxLength(128)]
        public string? VectorStoreFileId { get; set; }
    }
}
