#pragma warning disable OPENAI001 // OpenAI Assistants API is in preview

using Azure.AI.OpenAI;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using OpenAI.Assistants;
using OpenAI.Files;
using OpenAI.VectorStores;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using twoSaaSCore.Data;
using twoSaaSCore.Models;

namespace twoSaaSCore.Services
{
    public class RoomAgentService : IRoomAgentService
    {
        public const string QueuedVectorStoreFileIdPrefix = "queued:";
        public const string InProgressVectorStoreFileIdPrefix = "inprogress:";
        public const string FailedVectorStoreFileIdPrefix = "failed:";

        private const int MaxLinkedPdfs = 20;
        private const long MaxPdfDownloadBytes = 50L * 1024 * 1024; // 50 MB

        private readonly AzureAiOptions _aiOptions;
        private readonly AzureBlobOptions _blobOptions;
        private readonly ApplicationDbContext _db;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<RoomAgentService> _logger;
        private readonly IRoomQaService _roomQaService;

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
            IHttpClientFactory httpClientFactory,
            IRoomQaService roomQaService,
            ILogger<RoomAgentService> logger)
        {
            _aiOptions = aiOptions.Value;
            _blobOptions = blobOptions.Value;
            _db = db;
            _httpClientFactory = httpClientFactory;
            _roomQaService = roomQaService;
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

        private IQueryable<RoomAgent> RoomAgentsQuery() => _db.RoomAgents.IgnoreQueryFilters();
        private IQueryable<RoomFileRef> RoomFileRefsQuery() => _db.RoomFileRefs.IgnoreQueryFilters();
        private IQueryable<RoomWebLink> RoomWebLinksQuery() => _db.RoomWebLinks.IgnoreQueryFilters();
        private IQueryable<RoomChatThread> RoomChatThreadsQuery() => _db.RoomChatThreads.IgnoreQueryFilters();
        private IQueryable<RoomChatMessage> RoomChatMessagesQuery() => _db.RoomChatMessages.IgnoreQueryFilters();

        public async Task<(string AgentId, string VectorStoreId)> EnsureAgentAsync(
            Guid tenantId, Guid roomId, CancellationToken ct = default)
        {
            if (!IsConfigured) throw new InvalidOperationException("Azure AI is not configured.");

            var existing = await RoomAgentsQuery()
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
                Instructions = BuildEffectiveInstructions(null),
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
                for (var i = 0; i < 5; i++)
                {
                    existing = await RoomAgentsQuery()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.RoomId == roomId, ct);
                    if (existing != null)
                        return (existing.AgentId, existing.VectorStoreId);
                    await Task.Delay(200, ct);
                }
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
                var inProgressRef = await RoomFileRefsQuery()
                    .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.RoomId == roomId && r.FileId == fileId, ct);
                if (inProgressRef == null)
                {
                    _logger.LogDebug("Skipping AI indexing for deleted/missing file {FileId} in room {RoomId}.", fileId, roomId);
                    return;
                }

                if (inProgressRef != null)
                {
                    inProgressRef.VectorStoreFileId = $"{InProgressVectorStoreFileIdPrefix}{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                    await _db.SaveChangesAsync(ct);
                }

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
                var fileRef = await RoomFileRefsQuery()
                    .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.RoomId == roomId && r.FileId == fileId, ct);
                if (fileRef != null)
                {
                    fileRef.VectorStoreFileId = vectorStoreFile.Value.FileId;
                    await _db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                var failedRef = await RoomFileRefsQuery()
                    .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.RoomId == roomId && r.FileId == fileId, ct);
                if (failedRef != null)
                {
                    failedRef.VectorStoreFileId = $"{FailedVectorStoreFileIdPrefix}{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                    await _db.SaveChangesAsync(ct);
                }

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
                var fileRef = await RoomFileRefsQuery()
                    .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.RoomId == roomId && r.FileId == fileId, ct);

                if (fileRef?.VectorStoreFileId == null) return;
                if (fileRef.VectorStoreFileId.StartsWith(QueuedVectorStoreFileIdPrefix, StringComparison.OrdinalIgnoreCase)) return;
                if (fileRef.VectorStoreFileId.StartsWith(InProgressVectorStoreFileIdPrefix, StringComparison.OrdinalIgnoreCase)) return;
                if (fileRef.VectorStoreFileId.StartsWith(FailedVectorStoreFileIdPrefix, StringComparison.OrdinalIgnoreCase)) return;

                var roomAgent = await RoomAgentsQuery()
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
                var roomAgent = await RoomAgentsQuery()
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

        private Kernel CreateKernel(Guid tenantId, Guid roomId, string userId, string? userEmail)
        {
            if (!IsConfigured) throw new InvalidOperationException("Azure AI is not configured.");

            var credentialOptions = new DefaultAzureCredentialOptions();
            if (!string.IsNullOrWhiteSpace(_aiOptions.TenantId))
            {
                credentialOptions.TenantId = _aiOptions.TenantId;
            }

            if (_aiOptions.ExcludeManagedIdentityInDevelopment &&
                string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase))
            {
                credentialOptions.ExcludeManagedIdentityCredential = true;
            }

            var builder = Kernel.CreateBuilder();
            builder.AddAzureOpenAIChatCompletion(
                deploymentName: _aiOptions.ChatModel,
                endpoint: _aiOptions.Endpoint,
                credentials: new DefaultAzureCredential(credentialOptions));

            var kernel = builder.Build();
            kernel.Plugins.AddFromObject(
                new RoomQaSkill(_roomQaService, tenantId, roomId, userId, userEmail),
                pluginName: "RoomQa");

            return kernel;
        }

        public async Task<ChatResponse> ChatAsync(
            Guid tenantId,
            Guid roomId,
            string userId,
            string? userEmail,
            string userMessage,
            string? threadId = null,
            CancellationToken ct = default)
        {
            if (!IsConfigured) throw new InvalidOperationException("Azure AI is not configured.");
            if (string.IsNullOrWhiteSpace(userMessage)) throw new ArgumentException("Message cannot be empty.", nameof(userMessage));
            if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User id is required.", nameof(userId));

            var roomThread = await GetOrCreateThreadAsync(tenantId, roomId, userId, threadId, ct);

            var historyRows = await RoomChatMessagesQuery()
                .Where(m => m.TenantId == tenantId && m.RoomId == roomId && m.ThreadId == roomThread.ThreadId)
                .OrderBy(m => m.CreatedUtc)
                .ToListAsync(ct);

            var systemInstructions = await GetSystemInstructionsAsync(tenantId, roomId, ct);

            var history = new ChatHistory();
            history.AddSystemMessage($"""
                {BuildEffectiveInstructions(systemInstructions)}

                You can create Q&A entries when asked by invoking the RoomQa.submit_room_question function.
                Use the function when the user asks to save or post a question into the room Q&A.
                """);

            foreach (var row in historyRows)
            {
                if (row.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                {
                    history.AddUserMessage(row.Content);
                }
                else if (row.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                {
                    history.AddAssistantMessage(row.Content);
                }
            }

            history.AddUserMessage(userMessage.Trim());

            var kernel = CreateKernel(tenantId, roomId, userId, userEmail);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var settings = new AzureOpenAIPromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
            };

            var response = await chat.GetChatMessageContentAsync(history, settings, kernel, ct);
            var responseText = string.IsNullOrWhiteSpace(response.Content)
                ? "No response was generated. Please try rephrasing your question."
                : response.Content!;

            _db.RoomChatMessages.Add(new RoomChatMessage
            {
                TenantId = tenantId,
                RoomId = roomId,
                ThreadId = roomThread.ThreadId,
                UserId = userId,
                Role = "user",
                Content = userMessage.Trim(),
                CreatedUtc = DateTimeOffset.UtcNow
            });

            _db.RoomChatMessages.Add(new RoomChatMessage
            {
                TenantId = tenantId,
                RoomId = roomId,
                ThreadId = roomThread.ThreadId,
                UserId = userId,
                Role = "assistant",
                Content = responseText,
                CreatedUtc = DateTimeOffset.UtcNow
            });

            roomThread.LastActivityUtc = DateTimeOffset.UtcNow;
            roomThread.MessageCount += 2;

            if (string.IsNullOrWhiteSpace(roomThread.Title))
            {
                var first = userMessage.Trim();
                roomThread.Title = first.Length <= 80 ? first : first[..80];
            }

            await _db.SaveChangesAsync(ct);

            return new ChatResponse(responseText, roomThread.ThreadId, []);
        }

        public async Task SaveThreadAsync(
            Guid tenantId,
            Guid roomId,
            string userId,
            string threadId,
            string? title,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User id is required.", nameof(userId));
            if (string.IsNullOrWhiteSpace(threadId)) throw new ArgumentException("Thread id is required.", nameof(threadId));

            var thread = await RoomChatThreadsQuery()
                .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.RoomId == roomId && t.UserId == userId && t.ThreadId == threadId, ct);

            if (thread == null)
            {
                throw new InvalidOperationException("Thread not found.");
            }

            thread.IsSaved = true;
            if (!string.IsNullOrWhiteSpace(title))
            {
                var trimmed = title.Trim();
                thread.Title = trimmed.Length <= 200 ? trimmed : trimmed[..200];
            }

            thread.LastActivityUtc = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        public async Task<List<ChatThreadSummary>> ListThreadsAsync(
            Guid tenantId,
            Guid roomId,
            string userId,
            bool savedOnly,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User id is required.", nameof(userId));

            var query = RoomChatThreadsQuery()
                .Where(t => t.TenantId == tenantId && t.RoomId == roomId && t.UserId == userId);

            if (savedOnly)
            {
                query = query.Where(t => t.IsSaved);
            }

            return await query
                .OrderByDescending(t => t.LastActivityUtc)
                .Select(t => new ChatThreadSummary(t.ThreadId, t.Title, t.LastActivityUtc, t.IsSaved, t.MessageCount))
                .ToListAsync(ct);
        }

        private async Task<RoomChatThread> GetOrCreateThreadAsync(
            Guid tenantId,
            Guid roomId,
            string userId,
            string? threadId,
            CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                var existing = await RoomChatThreadsQuery()
                    .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.RoomId == roomId && t.UserId == userId && t.ThreadId == threadId, ct);
                if (existing != null)
                {
                    return existing;
                }
            }

            var newThread = new RoomChatThread
            {
                TenantId = tenantId,
                RoomId = roomId,
                UserId = userId,
                ThreadId = Guid.NewGuid().ToString("N"),
                IsSaved = false,
                MessageCount = 0,
                CreatedUtc = DateTimeOffset.UtcNow,
                LastActivityUtc = DateTimeOffset.UtcNow
            };

            _db.RoomChatThreads.Add(newThread);
            await _db.SaveChangesAsync(ct);
            return newThread;
        }

        private async Task<string?> ResolveFileNameAsync(
            Guid tenantId, Guid roomId, string openAiFileId, CancellationToken ct)
        {
            var fileRef = await RoomFileRefsQuery()
                .FirstOrDefaultAsync(r =>
                    r.TenantId == tenantId && r.RoomId == roomId && r.VectorStoreFileId == openAiFileId, ct);
            return fileRef?.OriginalFileName;
        }

        public async Task<Dictionary<Guid, AiIndexingStatus>> GetIndexingStatusesAsync(
            Guid tenantId, Guid roomId, IReadOnlyList<Guid> fileIds, CancellationToken ct = default)
        {
            var result = new Dictionary<Guid, AiIndexingStatus>();
            if (!IsConfigured || fileIds.Count == 0) return result;

            var roomAgent = await RoomAgentsQuery()
                .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.RoomId == roomId, ct);

            var fileRefs = await RoomFileRefsQuery()
                .Where(r => r.TenantId == tenantId && r.RoomId == roomId && fileIds.Contains(r.FileId))
                .ToListAsync(ct);

            foreach (var fr in fileRefs)
            {
                if (string.IsNullOrEmpty(fr.VectorStoreFileId))
                {
                    result[fr.FileId] = AiIndexingStatus.None;
                    continue;
                }

                if (fr.VectorStoreFileId.StartsWith(QueuedVectorStoreFileIdPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    result[fr.FileId] = AiIndexingStatus.Queued;
                    continue;
                }

                if (fr.VectorStoreFileId.StartsWith(InProgressVectorStoreFileIdPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    result[fr.FileId] = AiIndexingStatus.InProgress;
                    continue;
                }

                if (fr.VectorStoreFileId.StartsWith(FailedVectorStoreFileIdPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    result[fr.FileId] = AiIndexingStatus.Failed;
                    continue;
                }

                if (roomAgent == null)
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
                        VectorStoreFileStatus.Completed => AiIndexingStatus.Completed,
                        VectorStoreFileStatus.Failed => AiIndexingStatus.Failed,
                        VectorStoreFileStatus.Cancelled => AiIndexingStatus.Cancelled,
                        _ => AiIndexingStatus.None
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

        /// <summary>Builds the effective instructions by combining defaults with any custom instructions.</summary>
        private static string BuildEffectiveInstructions(string? customInstructions)
        {
            if (string.IsNullOrWhiteSpace(customInstructions))
                return AgentInstructions;

            return $"""
                {AgentInstructions}

                Additional instructions for this room:
                {customInstructions.Trim()}
                """;
        }

        public async Task<string?> GetSystemInstructionsAsync(
            Guid tenantId, Guid roomId, CancellationToken ct = default)
        {
            var agent = await RoomAgentsQuery()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.RoomId == roomId, ct);
            return agent?.SystemInstructions;
        }

        public async Task UpdateSystemInstructionsAsync(
            Guid tenantId, Guid roomId, string? instructions, CancellationToken ct = default)
        {
            var trimmed = string.IsNullOrWhiteSpace(instructions) ? null : instructions.Trim();

            var agent = await RoomAgentsQuery()
                .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.RoomId == roomId, ct);

            if (agent == null)
            {
                var _ = await EnsureAgentAsync(tenantId, roomId, ct);
                agent = await RoomAgentsQuery()
                    .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.RoomId == roomId, ct);
                if (agent == null) return;
            }

            agent.SystemInstructions = trimmed;
            await _db.SaveChangesAsync(ct);

            try
            {
                EnsureClients();
                var effective = BuildEffectiveInstructions(trimmed);
                await _assistantClient!.ModifyAssistantAsync(
                    agent.AgentId,
                    new AssistantModificationOptions { Instructions = effective },
                    ct);
                _logger.LogInformation("Updated system instructions for assistant {AgentId} in room {RoomId}.", agent.AgentId, roomId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to push updated instructions to assistant for room {RoomId}.", roomId);
            }
        }

        public async Task IndexWebLinkAsync(
            Guid tenantId, Guid roomId, Guid linkId, string url,
            CancellationToken ct = default)
        {
            if (!IsConfigured) return;

            try
            {
                var link = await RoomWebLinksQuery()
                    .FirstOrDefaultAsync(w => w.TenantId == tenantId && w.RoomId == roomId && w.LinkId == linkId, ct);
                if (link == null)
                {
                    _logger.LogDebug("Skipping web link indexing for deleted/missing link {LinkId}.", linkId);
                    return;
                }

                link.VectorStoreFileId = $"{InProgressVectorStoreFileIdPrefix}{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                await _db.SaveChangesAsync(ct);

                var (_, vectorStoreId) = await EnsureAgentAsync(tenantId, roomId, ct);
                EnsureClients();

                using var httpClient = _httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VaultRoom/1.0");

                using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                var bodyBytes = await DownloadWithSizeLimitAsync(response, MaxPdfDownloadBytes, ct);

                int pdfCount = 0;
                string? mainFileId = null;

                if (contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase) ||
                    url.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var pdfFileName = ExtractFileNameFromUrl(url, ".pdf");
                    mainFileId = await UploadPdfToVectorStoreAsync(
                        tenantId, roomId, linkId, url, link.AddedByUserId, vectorStoreId, bodyBytes, pdfFileName, ct);
                    pdfCount = 1;
                }
                else
                {
                    var html = Encoding.UTF8.GetString(bodyBytes);
                    var pageTitle = ExtractPageTitle(html) ?? url;
                    var pageText = ExtractTextFromHtml(html);

                    if (!string.IsNullOrWhiteSpace(pageText))
                    {
                        var txtFileName = SanitizeFileName(pageTitle, ".txt");
                        using var txtStream = new MemoryStream(Encoding.UTF8.GetBytes(pageText));
                        var uploaded = await _fileClient!.UploadFileAsync(
                            txtStream, txtFileName, FileUploadPurpose.Assistants, ct);
                        var vsFile = await _vectorStoreClient!.AddFileToVectorStoreAsync(
                            vectorStoreId, uploaded.Value.Id, ct);
                        mainFileId = vsFile.Value.FileId;

                        _logger.LogInformation("Indexed web page text from {Url} as {FileName}.", url, txtFileName);
                    }

                    var pdfLinks = DiscoverPdfLinks(html, url);
                    foreach (var pdfUrl in pdfLinks.Take(MaxLinkedPdfs))
                    {
                        try
                        {
                            using var pdfResponse = await httpClient.GetAsync(pdfUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                            if (!pdfResponse.IsSuccessStatusCode) continue;

                            var pdfBytes = await DownloadWithSizeLimitAsync(pdfResponse, MaxPdfDownloadBytes, ct);
                            if (pdfBytes.Length == 0) continue;

                            var pdfFileName = ExtractFileNameFromUrl(pdfUrl, ".pdf");
                            await UploadPdfToVectorStoreAsync(
                                tenantId, roomId, linkId, pdfUrl, link.AddedByUserId, vectorStoreId, pdfBytes, pdfFileName, ct);
                            pdfCount++;

                            _logger.LogInformation("Indexed linked PDF {PdfUrl} from page {Url}.", pdfUrl, url);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to download linked PDF {PdfUrl} from page {Url}.", pdfUrl, url);
                        }
                    }

                    link = await RoomWebLinksQuery()
                        .FirstOrDefaultAsync(w => w.TenantId == tenantId && w.RoomId == roomId && w.LinkId == linkId, ct);
                    if (link != null && !string.IsNullOrWhiteSpace(pageTitle) && pageTitle != url)
                    {
                        link.Title = pageTitle.Length > 256 ? pageTitle[..256] : pageTitle;
                    }
                }

                link = await RoomWebLinksQuery()
                    .FirstOrDefaultAsync(w => w.TenantId == tenantId && w.RoomId == roomId && w.LinkId == linkId, ct);
                if (link != null)
                {
                    link.VectorStoreFileId = mainFileId;
                    link.LinkedPdfCount = pdfCount;
                    link.LastFetchedUtc = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                var failedLink = await RoomWebLinksQuery()
                    .FirstOrDefaultAsync(w => w.TenantId == tenantId && w.RoomId == roomId && w.LinkId == linkId, ct);
                if (failedLink != null)
                {
                    failedLink.VectorStoreFileId = $"{FailedVectorStoreFileIdPrefix}{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                    await _db.SaveChangesAsync(ct);
                }

                _logger.LogWarning(ex, "Failed to index web link {LinkId} ({Url}) for room {RoomId}.", linkId, url, roomId);
            }
        }

        private async Task<string?> UploadPdfToVectorStoreAsync(
            Guid tenantId,
            Guid roomId,
            Guid linkId,
            string sourcePdfUrl,
            string? addedByUserId,
            string vectorStoreId,
            byte[] pdfBytes,
            string pdfFileName,
            CancellationToken ct)
        {
            await PersistWebDownloadedPdfAsync(tenantId, roomId, linkId, sourcePdfUrl, addedByUserId, pdfBytes, pdfFileName, ct);

            using var pdfStream = new MemoryStream(pdfBytes, writable: false);
            var uploaded = await _fileClient!.UploadFileAsync(
                pdfStream, pdfFileName, FileUploadPurpose.Assistants, ct);
            var vsFile = await _vectorStoreClient!.AddFileToVectorStoreAsync(
                vectorStoreId, uploaded.Value.Id, ct);

            var ocrText = await TryExtractPdfTextWithOcrAsync(pdfBytes, ct);
            if (!string.IsNullOrWhiteSpace(ocrText))
            {
                var ocrFileName = $"{Path.GetFileNameWithoutExtension(pdfFileName)}.ocr.txt";
                using var ocrStream = new MemoryStream(Encoding.UTF8.GetBytes(ocrText));
                var ocrUploaded = await _fileClient!.UploadFileAsync(
                    ocrStream, ocrFileName, FileUploadPurpose.Assistants, ct);
                await _vectorStoreClient!.AddFileToVectorStoreAsync(
                    vectorStoreId, ocrUploaded.Value.Id, ct);
            }

            return vsFile.Value.FileId;
        }

        private async Task PersistWebDownloadedPdfAsync(
            Guid tenantId,
            Guid roomId,
            Guid linkId,
            string sourcePdfUrl,
            string? addedByUserId,
            byte[] pdfBytes,
            string pdfFileName,
            CancellationToken ct)
        {
            var blobName = BuildWebDownloadedPdfBlobName(tenantId, roomId, linkId, sourcePdfUrl, pdfFileName);

            var existing = await RoomFileRefsQuery()
                .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.RoomId == roomId && r.BlobName == blobName, ct);

            if (existing == null)
            {
                _db.RoomFileRefs.Add(new RoomFileRef
                {
                    TenantId = tenantId,
                    RoomId = roomId,
                    FileId = Guid.NewGuid(),
                    BlobName = blobName,
                    OriginalFileName = pdfFileName,
                    Size = pdfBytes.LongLength,
                    ContentType = "application/pdf",
                    FolderPath = null,
                    AddedUtc = DateTimeOffset.UtcNow,
                    AddedByUserId = addedByUserId
                });
            }
            else
            {
                existing.Size = pdfBytes.LongLength;
                existing.ContentType = "application/pdf";
                if (string.IsNullOrWhiteSpace(existing.OriginalFileName))
                    existing.OriginalFileName = pdfFileName;
            }

            var blobService = new BlobServiceClient(_blobOptions.ConnectionString);
            var container = blobService.GetBlobContainerClient(_blobOptions.Container);
            var blob = container.GetBlobClient(blobName);

            using var stream = new MemoryStream(pdfBytes, writable: false);
            await blob.UploadAsync(stream, overwrite: true, cancellationToken: ct);
            await blob.SetHttpHeadersAsync(new BlobHttpHeaders { ContentType = "application/pdf" }, cancellationToken: ct);

            await _db.SaveChangesAsync(ct);
        }

        private static string BuildWebDownloadedPdfBlobName(
            Guid tenantId,
            Guid roomId,
            Guid linkId,
            string sourcePdfUrl,
            string pdfFileName)
        {
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sourcePdfUrl));
            var hash = Convert.ToHexString(hashBytes).ToLowerInvariant()[..16];
            var safeName = SanitizeFileName(pdfFileName, ".pdf");
            return $"{tenantId}/{roomId}/web-links/{linkId}/{hash}_{safeName}";
        }

        private static List<string> DiscoverPdfLinks(string html, string baseUrl)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var baseUri = new Uri(baseUrl);

            foreach (Match m in Regex.Matches(
                html,
                @"<a\s[^>]*href\s*=\s*[""']([^""']+\.pdf)(?:[?#][^""']*)?[""']",
                RegexOptions.IgnoreCase | RegexOptions.Compiled,
                TimeSpan.FromSeconds(5)))
            {
                var href = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                if (Uri.TryCreate(baseUri, href, out var absolute) &&
                    (absolute.Scheme == "http" || absolute.Scheme == "https"))
                {
                    results.Add(absolute.AbsoluteUri);
                }
            }

            return results.ToList();
        }

        private static async Task<byte[]> DownloadWithSizeLimitAsync(
            HttpResponseMessage response, long maxBytes, CancellationToken ct)
        {
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > maxBytes)
                return [];

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var buf = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await stream.ReadAsync(buf, ct)) > 0)
            {
                totalRead += read;
                if (totalRead > maxBytes)
                    return [];
                buffer.Write(buf, 0, read);
            }

            return buffer.ToArray();
        }

        private static string ExtractTextFromHtml(string html)
        {
            var cleaned = Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"<[^>]+>", " ");
            cleaned = System.Net.WebUtility.HtmlDecode(cleaned);
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            return cleaned;
        }

        private static string? ExtractPageTitle(string html)
        {
            var match = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) return null;
            var title = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }

        private static string ExtractFileNameFromUrl(string url, string fallbackExtension)
        {
            try
            {
                var uri = new Uri(url);
                var fileName = Path.GetFileName(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(fileName) && fileName.Contains('.'))
                    return SanitizeFileName(fileName, fallbackExtension);
            }
            catch { }

            return $"weblink-{Guid.NewGuid():N}{fallbackExtension}";
        }

        private static string SanitizeFileName(string name, string fallbackExtension)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
            if (sanitized.Length > 100)
                sanitized = sanitized[..100];
            if (!Path.HasExtension(sanitized))
                sanitized += fallbackExtension;
            return sanitized;
        }

        public async Task RemoveWebLinkFromVectorStoreAsync(
            Guid tenantId, Guid roomId, Guid linkId, CancellationToken ct = default)
        {
            if (!IsConfigured) return;

            try
            {
                var link = await RoomWebLinksQuery()
                    .FirstOrDefaultAsync(w => w.TenantId == tenantId && w.RoomId == roomId && w.LinkId == linkId, ct);

                if (link?.VectorStoreFileId == null) return;
                if (link.VectorStoreFileId.StartsWith(QueuedVectorStoreFileIdPrefix, StringComparison.OrdinalIgnoreCase)) return;
                if (link.VectorStoreFileId.StartsWith(InProgressVectorStoreFileIdPrefix, StringComparison.OrdinalIgnoreCase)) return;
                if (link.VectorStoreFileId.StartsWith(FailedVectorStoreFileIdPrefix, StringComparison.OrdinalIgnoreCase)) return;

                var roomAgent = await RoomAgentsQuery()
                    .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.RoomId == roomId, ct);

                if (roomAgent == null) return;

                EnsureClients();

                await _vectorStoreClient!.RemoveFileFromVectorStoreAsync(
                    roomAgent.VectorStoreId, link.VectorStoreFileId, ct);

                _logger.LogInformation(
                    "Removed web link {LinkId} (OpenAI: {OpenAiFileId}) from vector store for room {RoomId}.",
                    linkId, link.VectorStoreFileId, roomId);

                link.VectorStoreFileId = null;
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove web link {LinkId} from vector store for room {RoomId}.", linkId, roomId);
            }
        }

        public async Task<Dictionary<Guid, AiIndexingStatus>> GetWebLinkIndexingStatusesAsync(
            Guid tenantId, Guid roomId, IReadOnlyList<Guid> linkIds, CancellationToken ct = default)
        {
            var result = new Dictionary<Guid, AiIndexingStatus>();
            if (!IsConfigured || linkIds.Count == 0) return result;

            var roomAgent = await RoomAgentsQuery()
                .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.RoomId == roomId, ct);

            var links = await RoomWebLinksQuery()
                .Where(w => w.TenantId == tenantId && w.RoomId == roomId && linkIds.Contains(w.LinkId))
                .ToListAsync(ct);

            foreach (var link in links)
            {
                if (string.IsNullOrEmpty(link.VectorStoreFileId))
                {
                    result[link.LinkId] = AiIndexingStatus.None;
                    continue;
                }

                if (link.VectorStoreFileId.StartsWith(QueuedVectorStoreFileIdPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    result[link.LinkId] = AiIndexingStatus.Queued;
                    continue;
                }
                if (link.VectorStoreFileId.StartsWith(InProgressVectorStoreFileIdPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    result[link.LinkId] = AiIndexingStatus.InProgress;
                    continue;
                }
                if (link.VectorStoreFileId.StartsWith(FailedVectorStoreFileIdPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    result[link.LinkId] = AiIndexingStatus.Failed;
                    continue;
                }

                if (roomAgent == null)
                {
                    result[link.LinkId] = AiIndexingStatus.None;
                    continue;
                }

                try
                {
                    EnsureClients();
                    var vsFile = await _vectorStoreClient!.GetVectorStoreFileAsync(
                        roomAgent.VectorStoreId, link.VectorStoreFileId, ct);

                    result[link.LinkId] = vsFile.Value.Status switch
                    {
                        VectorStoreFileStatus.InProgress => AiIndexingStatus.InProgress,
                        VectorStoreFileStatus.Completed => AiIndexingStatus.Completed,
                        VectorStoreFileStatus.Failed => AiIndexingStatus.Failed,
                        VectorStoreFileStatus.Cancelled => AiIndexingStatus.Cancelled,
                        _ => AiIndexingStatus.None
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not get vector store status for web link {LinkId}.", link.LinkId);
                    result[link.LinkId] = AiIndexingStatus.None;
                }
            }

            return result;
        }
    }
}
