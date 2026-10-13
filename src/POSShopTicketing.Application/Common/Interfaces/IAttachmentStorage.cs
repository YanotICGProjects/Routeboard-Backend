using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Application.Common.Interfaces
{
    public interface IAttachmentStorage
    {
        /// <summary>
        /// Stores bytes and returns a storage key.
        /// </summary>
        Task<string> SaveAsync(
            Guid tenantId,
            Guid ticketId,
            string fileName,
            byte[] content,
            CancellationToken ct);

        Task<Stream> OpenReadAsync(
            string storageKey,
            CancellationToken ct);
    }
}
