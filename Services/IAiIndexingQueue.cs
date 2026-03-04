using System;

namespace twoSaaSCore.Services
{
    public interface IAiIndexingQueue
    {
        void Enqueue(Guid tenantId, Guid roomId, Guid fileId, string blobName, string fileName);

        void EnqueueWebLink(Guid tenantId, Guid roomId, Guid linkId, string url);
    }
}
