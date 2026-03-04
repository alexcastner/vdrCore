using System;
using System.ComponentModel.DataAnnotations;

namespace twoSaaSCore.Models
{
    /// <summary>
    /// Tracks a web link added as an AI knowledge source for a room.
    /// HTML text and discovered PDFs are indexed into the room's vector store.
    /// </summary>
    public class RoomWebLink : ITenantEntity
    {
        public int Id { get; set; }

        public Guid TenantId { get; set; }

        public Guid RoomId { get; set; }

        public Guid LinkId { get; set; }

        /// <summary>The URL of the web page.</summary>
        [MaxLength(2048)]
        public string Url { get; set; } = default!;

        /// <summary>Page title extracted from HTML, or the URL if unavailable.</summary>
        [MaxLength(256)]
        public string? Title { get; set; }

        /// <summary>
        /// Azure OpenAI file ID for the indexed HTML text, or a sentinel prefix
        /// (<c>queued:</c>, <c>inprogress:</c>, <c>failed:</c>) tracking indexing lifecycle.
        /// </summary>
        [MaxLength(128)]
        public string? VectorStoreFileId { get; set; }

        /// <summary>Number of PDF files discovered and indexed from the page.</summary>
        public int LinkedPdfCount { get; set; }

        [MaxLength(450)]
        public string? AddedByUserId { get; set; }

        public DateTimeOffset AddedUtc { get; set; }

        public DateTimeOffset? LastFetchedUtc { get; set; }
    }
}
