using System;
using System.ComponentModel.DataAnnotations;
using twoSaaSCore.Models;

namespace twoSaaSCore.Models
{
    public class TenantFile : ITenantEntity
    {
        public int Id { get; set; }
        public Guid TenantId { get; set; }

        [Required]
        [MaxLength(260)]
        public string FileName { get; set; } = string.Empty;

        [MaxLength(200)]
        public string? ContentType { get; set; }

        [Required]
        [MaxLength(1024)]
        public string BlobName { get; set; } = string.Empty; // container-relative path, e.g., tenantId/guid_filename

        [MaxLength(2048)]
        public string? BlobUri { get; set; }

        public long Size { get; set; }
        public DateTimeOffset UploadedAt { get; set; }

        [MaxLength(450)]
        public string? UploadedByUserId { get; set; }
    }
}
