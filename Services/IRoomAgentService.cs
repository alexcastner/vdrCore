using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace twoSaaSCore.Services
{
    public record ChatResponse(string Message, string ThreadId, List<ChatCitation> Citations);
    public record ChatCitation(string FileName, string? Snippet);

    /// <summary>Saved thread summary for room chat history UI.</summary>
    public record ChatThreadSummary(string ThreadId, string? Title, DateTimeOffset LastActivityUtc, bool IsSaved, int MessageCount);

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
        Task<ChatResponse> ChatAsync(Guid tenantId, Guid roomId, string userId, string? userEmail, string userMessage, string? threadId = null, CancellationToken ct = default);

        /// <summary>Marks a thread as saved and optionally updates the thread title.</summary>
        Task SaveThreadAsync(Guid tenantId, Guid roomId, string userId, string threadId, string? title, CancellationToken ct = default);

        /// <summary>Lists chat threads for a user in a room.</summary>
        Task<List<ChatThreadSummary>> ListThreadsAsync(Guid tenantId, Guid roomId, string userId, bool savedOnly, CancellationToken ct = default);

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
