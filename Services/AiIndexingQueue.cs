using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using twoSaaSCore.Data;

namespace twoSaaSCore.Services
{
    public sealed class AiIndexingQueue : BackgroundService, IAiIndexingQueue
    {
        private readonly Channel<IndexingWorkItem> _channel =
            Channel.CreateUnbounded<IndexingWorkItem>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AiIndexingQueue> _logger;

        public AiIndexingQueue(IServiceScopeFactory scopeFactory, ILogger<AiIndexingQueue> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public void Enqueue(Guid tenantId, Guid roomId, Guid fileId, string blobName, string fileName)
        {
            if (tenantId == Guid.Empty || roomId == Guid.Empty || fileId == Guid.Empty ||
                string.IsNullOrWhiteSpace(blobName) || string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            if (!_channel.Writer.TryWrite(new IndexingWorkItem(IndexingItemKind.File, tenantId, roomId, fileId, blobName, fileName, null)))
            {
                _logger.LogWarning("Failed to enqueue AI indexing task for file {FileId}.", fileId);
            }
        }

        public void EnqueueWebLink(Guid tenantId, Guid roomId, Guid linkId, string url)
        {
            if (tenantId == Guid.Empty || roomId == Guid.Empty || linkId == Guid.Empty ||
                string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (!_channel.Writer.TryWrite(new IndexingWorkItem(IndexingItemKind.WebLink, tenantId, roomId, linkId, null, null, url)))
            {
                _logger.LogWarning("Failed to enqueue AI indexing task for web link {LinkId}.", linkId);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await RecoverPendingWorkAsync(stoppingToken);

            await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var agentService = scope.ServiceProvider.GetRequiredService<IRoomAgentService>();

                    switch (item.Kind)
                    {
                        case IndexingItemKind.File:
                            await agentService.UploadFileToVectorStoreAsync(
                                item.TenantId, item.RoomId, item.ItemId, item.BlobName!, item.FileName!, stoppingToken);
                            break;

                        case IndexingItemKind.WebLink:
                            await agentService.IndexWebLinkAsync(
                                item.TenantId, item.RoomId, item.ItemId, item.Url!, stoppingToken);
                            break;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Background AI indexing failed for {Kind} {ItemId} in room {RoomId}.",
                        item.Kind, item.ItemId, item.RoomId);
                }
            }
        }

        private async Task RecoverPendingWorkAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Recover pending file indexing items
                var pendingFiles = await db.RoomFileRefs
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(r => r.VectorStoreFileId != null &&
                        (r.VectorStoreFileId.StartsWith(RoomAgentService.QueuedVectorStoreFileIdPrefix) ||
                         r.VectorStoreFileId.StartsWith(RoomAgentService.InProgressVectorStoreFileIdPrefix)))
                    .Select(r => new IndexingWorkItem(
                        IndexingItemKind.File,
                        r.TenantId,
                        r.RoomId,
                        r.FileId,
                        r.BlobName,
                        r.OriginalFileName,
                        null))
                    .ToListAsync(ct);

                foreach (var item in pendingFiles)
                {
                    _channel.Writer.TryWrite(item);
                }

                // Recover pending web link indexing items
                var pendingLinks = await db.RoomWebLinks
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(w => w.VectorStoreFileId != null &&
                        (w.VectorStoreFileId.StartsWith(RoomAgentService.QueuedVectorStoreFileIdPrefix) ||
                         w.VectorStoreFileId.StartsWith(RoomAgentService.InProgressVectorStoreFileIdPrefix)))
                    .Select(w => new IndexingWorkItem(
                        IndexingItemKind.WebLink,
                        w.TenantId,
                        w.RoomId,
                        w.LinkId,
                        null,
                        null,
                        w.Url))
                    .ToListAsync(ct);

                foreach (var item in pendingLinks)
                {
                    _channel.Writer.TryWrite(item);
                }

                var total = pendingFiles.Count + pendingLinks.Count;
                if (total > 0)
                {
                    _logger.LogInformation(
                        "Recovered {Count} pending AI indexing item(s) from storage ({Files} files, {Links} web links).",
                        total, pendingFiles.Count, pendingLinks.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to recover pending AI indexing items.");
            }
        }

        private enum IndexingItemKind { File, WebLink }

        private readonly record struct IndexingWorkItem(
            IndexingItemKind Kind,
            Guid TenantId,
            Guid RoomId,
            Guid ItemId,
            string? BlobName,
            string? FileName,
            string? Url);
    }
}
