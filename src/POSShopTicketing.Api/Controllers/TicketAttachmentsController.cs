using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Tickets.Queries.GetTicketAttachment;
using POSShopTicketing.Shared.Wrappers;

namespace POSShopTicketing.Api.Controllers
{


    [ApiController]
    [Authorize]
    public class TicketAttachmentsController : ControllerBase
    {
        private readonly IApplicationDbContext _context;
        private readonly IAttachmentStorage _storage;

        public TicketAttachmentsController(
            IApplicationDbContext context,
            IAttachmentStorage storage)
        {
            _context = context;
            _storage = storage;
        }

        [HttpGet("/api/tickets/{ticketId:guid}/attachments")]
        public async Task<ActionResult<ApiResponse<List<TicketAttachmentDto>>>> List(
            Guid ticketId,
            CancellationToken ct)
        {
            var items = await _context.TicketAttachments
                .AsNoTracking()
                .Where(a => a.TicketId == ticketId)
                .OrderBy(a => a.CreatedAt)
                .Select(a => new TicketAttachmentDto(
                    a.Id,
                    a.FileName,
                    a.ContentType,
                    a.SizeBytes,
                    a.IsInline))
                .ToListAsync(ct);

            return Ok(
                ApiResponse<List<TicketAttachmentDto>>
                .Success(items));
        }

        [HttpGet("/api/tickets/{ticketId:guid}/attachments/{attachmentId:guid}")]
        public async Task<IActionResult> Download(
            Guid ticketId,
            Guid attachmentId,
            CancellationToken ct)
        {
            var attachment = await _context.TicketAttachments
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    a => a.Id == attachmentId &&
                         a.TicketId == ticketId,
                    ct);

            if (attachment is null)
            {
                return NotFound();
            }

            var stream = await _storage.OpenReadAsync(
                attachment.StorageKey,
                ct);

            Response.Headers["X-Content-Type-Options"] = "nosniff";

            return File(
                stream,
                "application/octet-stream",
                attachment.FileName);
        }
    }
}