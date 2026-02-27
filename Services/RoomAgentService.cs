#pragma warning disable OPENAI001 // OpenAI Assistants API is in preview

using Azure.AI.OpenAI;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Assistants;
using OpenAI.Files;
using OpenAI.VectorStores;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using twoSaaSCore.Data;
using twoSaaSCore.Models;

namespace twoSaaSCore.Services
{
    public class RoomAgentService : IRoomAgentService
    {
        private readonly AzureAiOptions _aiOptions;
        private readonly AzureBlobOptions _blobOptions;
        private readonly ApplicationDbContext _db;
        private readonly ILogger<RoomAgentService> _logger;

        private AzureOpenAIClient? _openAiClient;
        private AssistantClient? _assistantClient;
        private VectorStoreClient? _vectorStoreClient;
        private OpenAIFileClient? _fileClient;
        private DocumentIntelligenceClient? _documentIntelligenceClient;

        private const string AgentInstructions =
            """
            You are a helpful document assistant for a secure virtual data room.
            Answer questions based solely on the uploaded documents in this room.
            If you cannot find relevant information in the documents, say so clearly.
            Always cite the specific document(s) and section(s) you reference.
            Be precise, professional, and concise.
            """;

        public bool IsConfigured { get; }

        public RoomAgentService(
            IOptions<AzureAiOptions> aiOptions,
            IOptions<AzureBlobOptions> blobOptions,
            ApplicationDbContext db,
            ILogger<RoomAgentService> logger)
        {
            _aiOptions = aiOptions.Value;
            _blobOptions = blobOptions.Value;
            _db = db;
            _logger = logger;
            IsConfigured = !string.IsNullOrWhiteSpace(_aiOptions.Endpoint);
        }

        private void EnsureClients()
        {
            if (_openAiClient != null) return;
            if (!IsConfigured) throw new InvalidOperationException("Azure AI is not configured.");

            var credentialOptions = new DefaultAzureCredentialOptions();
            if (!string.IsNullOrWhiteSpace(_aiOptions.TenantId))
                credentialOptions.TenantId = _aiOptions.TenantId;

            if (_aiOptions.ExcludeManagedIdentityInDevelopment &&
                string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase))
            {
                credentialOptions.ExcludeManagedIdentityCredential = true;
            }

            _openAiClient = new AzureOpenAIClient(
                new Uri(_aiOptions.Endpoint),
                new DefaultAzureCredential(credentialOptions),
                new AzureOpenAIClientOptions(AzureOpenAIClientOptions.ServiceVersion.V2025_04_01_Preview));

            _assistantClient = _openAiClient.GetAssistantClient();
            _vectorStoreClient = _openAiClient.GetVectorStoreClient();
            _fileClient = _openAiClient.GetOpenAIFileClient();
        }

        private bool CanUseOcrForPdf()
        {
            return _aiOptions.EnablePdfOcrFallback
                && !string.IsNullOrWhiteSpace(_aiOptions.OcrEndpoint)
                && !string.IsNullOrWhiteSpace(_aiOptions.OcrApiKey);
        }

        private void EnsureOcrClient()
        {
            if (_documentIntelligenceClient != null) return;
            if (!CanUseOcrForPdf()) throw new InvalidOperationException("PDF OCR fallback is not configured.");

            _documentIntelligenceClient = new DocumentIntelligenceClient(
                new Uri(_aiOptions.OcrEndpoint!),
                new AzureKeyCredential(_aiOptions.OcrApiKey!));
        }

        private async Task<string?> TryExtractPdfTextWithOcrAsync(byte[] pdfBytes, CancellationToken ct)
        {
            if (!CanUseOcrForPdf() || pdfBytes.Length == 0) return null;

            try
            {
                EnsureOcrClient();

                var operation = await _documentIntelligenceClient!.AnalyzeDocumentAsync(
                    WaitUntil.Completed,
                    _aiOptions.OcrModel,
                    BinaryData.FromBytes(pdfBytes),
                    cancellationToken: ct);

                var text = operation.Value.Content;
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PDF OCR fallback failed.");
                return null;
            }
        }

        public async Task<(string AgentId, string VectorStoreId)> EnsureAgentAsync(
            Guid tenantId, Guid roomId, CancellationToken ct = default)
        {
            if (!IsConfigured) throw new InvalidOperationException("Azure AI is not configured.");

            var existing = await _db.RoomAgents
                .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.RoomId == roomId, ct);

            if (existing != null)
                return (existing.AgentId, existing.VectorStoreId);

            EnsureClients();

            // Create vector store
            var vs = await _vectorStoreClient!.CreateVectorStoreAsync(
                new VectorStoreCreationOptions
                {
                    Name = $"vdr-{tenantId:N}-{roomId:N}"
                }, ct);

            _logger.LogInformation("Created vector store {VsId} for room {RoomId}", vs.Value.Id, roomId);

            // Create assistant with file_search tool
            var options = new AssistantCreationOptions
            {
                Name = $"VaultRoom Agent ({roomId:N})",
                Instructions = AgentInstructions,
                Tools = { new FileSearchToolDefinition() },
                ToolResources = new()
                {
                    FileSearch = new()
                    {
                        VectorStoreIds = { vs.Value.Id }
                    }
                }
            };

            var agent = await _assistantClient!.CreateAssistantAsync(_aiOptions.ChatModel, options, ct);

            _logger.LogInformation("Created assistant {AgentId} for room {RoomId}", agent.Value.Id, roomId);

            // Persist to DB
            var roomAgent = new RoomAgent
            {
                TenantId = tenantId,
                RoomId = roomId,
                AgentId = agent.Value.Id,
                VectorStoreId = vs.Value.Id,
                CreatedUtc = DateTimeOffset.UtcNow
            };

            _db.RoomAgents.Add(roomAgent);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                _db.Entry(roomAgent).State = EntityState.Detached;
                existing = await _db.RoomAgents
                    .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.RoomId == roomId, ct);
                if (existing != null)
                    return (existing.AgentId, existing.VectorStoreId);
                throw;
            }

            return (agent.Value.Id, vs.Value.Id);
        }

        public async Task UploadFileToVectorStoreAsync(
            Guid tenantId, Guid roomId, Guid fileId, string blobName, string fileName,
            CancellationToken ct = default)
        {
            if (!IsConfigured) return;

            try
            {
                var (_, vectorStoreId) = await EnsureAgentAsync(tenantId, roomId, ct);
                EnsureClients();

                // Download blob content
                var blobService = new BlobServiceClient(_blobOptions.ConnectionString);
                var container = blobService.GetBlobContainerClient(_blobOptions.Container);
                var blob = container.GetBlobClient(blobName);
                var download = await blob.DownloadStreamingAsync(cancellationToken: ct);

                // Buffer content so it can be reused for OCR fallback.
                using var stream = download.Value.Content;
                using var contentBuffer = new MemoryStream();
                await stream.CopyToAsync(contentBuffer, ct);
                var fileBytes = contentBuffer.ToArray();

                // Upload original to Azure OpenAI Files
                using var uploadStream = new MemoryStream(fileBytes, writable: false);
                var uploaded = await _fileClient!.UploadFileAsync(
                    uploadStream, fileName, FileUploadPurpose.Assistants, ct);

                _logger.LogInformation("Uploaded file {FileName} ({FileId}) as OpenAI file {OpenAiFileId}",
                    fileName, fileId, uploaded.Value.Id);

                // Add to vector store
                var vectorStoreFile = await _vectorStoreClient!.AddFileToVectorStoreAsync(
                    vectorStoreId, uploaded.Value.Id, ct);

                // OCR fallback for scanned PDFs: upload extracted text as an additional indexed file.
                if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var ocrText = await TryExtractPdfTextWithOcrAsync(fileBytes, ct);
                    if (!string.IsNullOrWhiteSpace(ocrText))
                    {
                        var ocrFileName = $"{Path.GetFileNameWithoutExtension(fileName)}.ocr.txt";
                        using var ocrStream = new MemoryStream(Encoding.UTF8.GetBytes(ocrText));
                        var ocrUploaded = await _fileClient!.UploadFileAsync(
                            ocrStream, ocrFileName, FileUploadPurpose.Assistants, ct);
                        await _vectorStoreClient!.AddFileToVectorStoreAsync(vectorStoreId, ocrUploaded.Value.Id, ct);

                        _logger.LogInformation("Added OCR text companion file for {FileName} ({FileId}).", fileName, fileId);
                    }
                }

                // Save OpenAI file ID to SQL reference (used for status lookups)
                var fileRef = await _db.RoomFileRefs
                    .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.RoomId == roomId && r.FileId == fileId, ct);
                if (fileRef != null)
                {
                    fileRef.VectorStoreFileId = vectorStoreFile.Value.FileId;
                    await _db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to upload file {FileId} to vector store for room {RoomId}. AI search may be incomplete.",
                    fileId, roomId);
            }
        }

        public async Task RemoveFileFromVectorStoreAsync(
            Guid tenantId, Guid roomId, Guid fileId, CancellationToken ct = default)
        {
            if (!IsConfigured) return;

            try
            {
                var fileRef = await _db.RoomFileRefs
                    .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.RoomId == roomId && r.FileId == fileId, ct);

                if (fileRef?.VectorStoreFileId == null) return;

                var roomAgent = await _db.RoomAgents
                    .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.RoomId == roomId, ct);

                if (roomAgent == null) return;

                EnsureClients();

                await _vectorStoreClient!.RemoveFileFromVectorStoreAsync(
                    roomAgent.VectorStoreId, fileRef.VectorStoreFileId, ct);

                _logger.LogInformation("Removed file {FileId} (OpenAI: {OpenAiFileId}) from vector store for room {RoomId}",
                    fileId, fileRef.VectorStoreFileId, roomId);

                fileRef.VectorStoreFileId = null;
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove file {FileId} from vector store for room {RoomId}.", fileId, roomId);
            }
        }

        public async Task DeleteAgentAsync(Guid tenantId, Guid roomId, CancellationToken ct = default)
        {
            if (!IsConfigured) return;

            try
            {
                var roomAgent = await _db.RoomAgents
                    .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.RoomId == roomId, ct);

                if (roomAgent == null) return;

                EnsureClients();

                await _assistantClient!.DeleteAssistantAsync(roomAgent.AgentId, ct);
                await _vectorStoreClient!.DeleteVectorStoreAsync(roomAgent.VectorStoreId, ct);

                _db.RoomAgents.Remove(roomAgent);
                await _db.SaveChangesAsync(ct);

                _logger.LogInformation("Deleted assistant {AgentId} and vector store {VsId} for room {RoomId}",
                    roomAgent.AgentId, roomAgent.VectorStoreId, roomId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up AI resources for room {RoomId}.", roomId);
            }
        }

        public async Task<ChatResponse> ChatAsync(
            Guid tenantId, Guid roomId, string userMessage, string? threadId = null,
            CancellationToken ct = default)
        {
            if (!IsConfigured) throw new InvalidOperationException("Azure AI is not configured.");

            var (agentId, _) = await EnsureAgentAsync(tenantId, roomId, ct);
            EnsureClients();

            // Create or reuse thread
            if (string.IsNullOrEmpty(threadId))
            {
                var thread = await _assistantClient!.CreateThreadAsync(cancellationToken: ct);
                threadId = thread.Value.Id;
            }

            // Send user message
            await _assistantClient!.CreateMessageAsync(
                threadId,
                MessageRole.User,
                [MessageContent.FromText(userMessage)],
                cancellationToken: ct);

            // Run the assistant
            var run = (await _assistantClient!.CreateRunAsync(threadId, agentId, cancellationToken: ct)).Value;

            // Poll for completion
            while (!run.Status.IsTerminal)
            {
                await Task.Delay(500, ct);
                run = (await _assistantClient!.GetRunAsync(threadId, run.Id, ct)).Value;
            }

            if (run.Status == RunStatus.Failed)
            {
                _logger.LogError("Assistant run failed for room {RoomId}: {Error}", roomId, run.LastError?.Message);
                return new ChatResponse(
                    "I'm sorry, I encountered an error processing your request. Please try again.",
                    threadId, []);
            }

            // Extract response — get the latest assistant message
            var responseText = "";
            var citations = new List<ChatCitation>();

            await foreach (var msg in _assistantClient!.GetMessagesAsync(
                threadId,
                new MessageCollectionOptions { Order = MessageCollectionOrder.Descending },
                ct))
            {
                if (msg.Role == MessageRole.Assistant)
                {
                    foreach (var content in msg.Content)
                    {
                        if (content.Text != null)
                        {
                            responseText = content.Text;
                            if (content.TextAnnotations != null)
                            {
                                foreach (var ann in content.TextAnnotations)
                                {
                                    if (ann.InputFileId != null)
                                    {
                                        var originalName = await ResolveFileNameAsync(
                                            tenantId, roomId, ann.InputFileId, ct);
                                        citations.Add(new ChatCitation(
                                            originalName ?? ann.InputFileId,
                                            ann.TextToReplace));
                                    }
                                }
                            }
                        }
                    }
                    break; // only need the latest assistant message
                }
            }

            if (string.IsNullOrEmpty(responseText))
            {
                return new ChatResponse(
                    "No response was generated. Please try rephrasing your question.",
                    threadId, []);
            }

            return new ChatResponse(responseText, threadId, citations);
        }

        private async Task<string?> ResolveFileNameAsync(
            Guid tenantId, Guid roomId, string openAiFileId, CancellationToken ct)
        {
            var fileRef = await _db.RoomFileRefs
                .FirstOrDefaultAsync(r =>
                    r.TenantId == tenantId && r.RoomId == roomId && r.VectorStoreFileId == openAiFileId, ct);
            return fileRef?.OriginalFileName;
        }

        public async Task<Dictionary<Guid, AiIndexingStatus>> GetIndexingStatusesAsync(
            Guid tenantId, Guid roomId, IReadOnlyList<Guid> fileIds, CancellationToken ct = default)
        {
            var result = new Dictionary<Guid, AiIndexingStatus>();
            if (!IsConfigured || fileIds.Count == 0) return result;

            var roomAgent = await _db.RoomAgents
                .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.RoomId == roomId, ct);

            var fileRefs = await _db.RoomFileRefs
                .Where(r => r.TenantId == tenantId && r.RoomId == roomId && fileIds.Contains(r.FileId))
                .ToListAsync(ct);

            foreach (var fr in fileRefs)
            {
                if (string.IsNullOrEmpty(fr.VectorStoreFileId) || roomAgent == null)
                {
                    result[fr.FileId] = AiIndexingStatus.None;
                    continue;
                }

                try
                {
                    EnsureClients();
                    var vsFile = await _vectorStoreClient!.GetVectorStoreFileAsync(
                        roomAgent.VectorStoreId, fr.VectorStoreFileId, ct);

                    result[fr.FileId] = vsFile.Value.Status switch
                    {
                        VectorStoreFileStatus.InProgress => AiIndexingStatus.InProgress,
                        VectorStoreFileStatus.Completed  => AiIndexingStatus.Completed,
                        VectorStoreFileStatus.Failed     => AiIndexingStatus.Failed,
                        VectorStoreFileStatus.Cancelled  => AiIndexingStatus.Cancelled,
                        _                                => AiIndexingStatus.None
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not get vector store status for file {FileId}", fr.FileId);
                    result[fr.FileId] = AiIndexingStatus.None;
                }
            }

            return result;
        }
    }
}
