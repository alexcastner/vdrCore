using System;
using System.ComponentModel.DataAnnotations;

namespace twoSaaSCore.Models
{
    /// <summary>
    /// Tracks the Azure AI Foundry Agent and Vector Store IDs for each room.
    /// One row per room. Created lazily on first chat or file upload.
    /// </summary>
    public class RoomAgent : ITenantEntity
    {
        public int Id { get; set; }

        public Guid TenantId { get; set; }

        public Guid RoomId { get; set; }

        /// <summary>Azure AI Foundry Agent ID.</summary>
        [MaxLength(128)]
        public string AgentId { get; set; } = string.Empty;

        /// <summary>Azure AI Foundry Vector Store ID.</summary>
        [MaxLength(128)]
        public string VectorStoreId { get; set; } = string.Empty;

        /// <summary>Optional custom system instructions for the AI assistant in this room.</summary>
        public string? SystemInstructions { get; set; }

        public DateTimeOffset CreatedUtc { get; set; }
    }
}
