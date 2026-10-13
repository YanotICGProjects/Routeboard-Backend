using POSShopTicketing.Domain.Common;
using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Domain.Entities
{
    /// <summary>
    /// Metadata about a stored file.
    /// The bytes live in IAttachmentStorage (disk / S3 / Azure Blob),
    /// not in the database.
    /// </summary>
    public class TicketAttachment : BaseAuditableEntity
    {

        public Guid TenantId { get; set; }

        public Guid TicketId { get; set; }

        public Guid TicketMessageId { get; set; }

        public string FileName { get; set; } = string.Empty;

        public string ContentType { get; set; }
            = "application/octet-stream";

        public long SizeBytes { get; set; }

        public string StorageKey { get; set; } = string.Empty;

        /// <summary>
        /// For inline images referenced as cid:xxx in the HTML body.
        /// </summary>
        public string? ContentId { get; set; }

        public bool IsInline { get; set; }

        public TicketMessage? TicketMessage { get; set; }
    }
}
