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
    }
}
