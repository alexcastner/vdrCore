using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace twoSaaSCore.Services
{
    public record ChatResponse(string Message, string ThreadId, List<ChatCitation> Citations);
    public record ChatCitation(string FileName, string? Snippet);

    /// <summary>Indexing status of a file in the vector store.</summary>
    public enum AiIndexingStatus
    {
        None,
        Queued,
        InProgress,
        Completed,
        Failed,
        Cancelled
    }

    public interface IRoomAgentService
    {
        bool IsConfigured { get; }
        Task<(string AgentId, string VectorStoreId)> EnsureAgentAsync(Guid tenantId, Guid roomId, CancellationToken ct = default);
        Task UploadFileToVectorStoreAsync(Guid tenantId, Guid roomId, Guid fileId, string blobName, string fileName, CancellationToken ct = default);
        Task RemoveFileFromVectorStoreAsync(Guid tenantId, Guid roomId, Guid fileId, CancellationToken ct = default);
        Task DeleteAgentAsync(Guid tenantId, Guid roomId, CancellationToken ct = default);
        Task<ChatResponse> ChatAsync(Guid tenantId, Guid roomId, string userMessage, string? threadId = null, CancellationToken ct = default);

        /// <summary>
        /// Checks the vector store indexing status for a batch of files.
        /// </summary>
        Task<Dictionary<Guid, AiIndexingStatus>> GetIndexingStatusesAsync(
            Guid tenantId, Guid roomId, IReadOnlyList<Guid> fileIds, CancellationToken ct = default);

        /// <summary>Gets the current custom system instructions for a room, or null if using defaults.</summary>
        Task<string?> GetSystemInstructionsAsync(Guid tenantId, Guid roomId, CancellationToken ct = default);

        /// <summary>Updates the custom system instructions and pushes them to the AI assistant.</summary>
        Task UpdateSystemInstructionsAsync(Guid tenantId, Guid roomId, string? instructions, CancellationToken ct = default);

        /// <summary>Indexes a web link into the room's vector store (HTML text + discovered PDFs).</summary>
        Task IndexWebLinkAsync(Guid tenantId, Guid roomId, Guid linkId, string url, CancellationToken ct = default);

        /// <summary>Removes a web link's indexed content from the vector store.</summary>
        Task RemoveWebLinkFromVectorStoreAsync(Guid tenantId, Guid roomId, Guid linkId, CancellationToken ct = default);

        /// <summary>Checks the vector store indexing status for a batch of web links.</summary>
        Task<Dictionary<Guid, AiIndexingStatus>> GetWebLinkIndexingStatusesAsync(
            Guid tenantId, Guid roomId, IReadOnlyList<Guid> linkIds, CancellationToken ct = default);
    }
}
