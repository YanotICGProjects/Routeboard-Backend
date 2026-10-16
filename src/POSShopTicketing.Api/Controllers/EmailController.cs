using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using POSShopTicketing.Api.Models.Email;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Infrastructure.Email;      // EmailSendException
using POSShopTicketing.Shared.Wrappers;

namespace POSShopTicketing.Api.Controllers;

/// <summary>
/// REPLACES Controllers/EmailController.cs (the whole file).
///
/// POST /api/email/send - the one endpoint that emails a customer about a ticket.
///
///   - messageTicketId is REQUIRED and the ticket must already exist. This endpoint never creates a ticket.
///   - kind "Notice"       an automatic no-reply message           (from no-reply__{workspace}@support...)
///   - kind "Conversation" an agent's message in the ticket thread (from the workspace address)
///   - the response returns the mailbox that was used and the thread ids (see TicketEmailResult)
///
/// The old free-form test endpoints (send anything to anyone, /provider, /send-app) are removed on purpose:
/// within the application every email must be tied to a ticket.
/// </summary>
[ApiController]
[Route("api/email")]
[Authorize(Roles = "Owner,Admin,Manager,Agent")]   // must match the role names in your TeamMemberRole enum
public class EmailController : ControllerBase
{
    private readonly ITicketEmailService _ticketEmail;
    private readonly ILogger<EmailController> _logger;

    public EmailController(ITicketEmailService ticketEmail, ILogger<EmailController> logger)
    {
        _ticketEmail = ticketEmail;
        _logger = logger;
    }

    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] SendEmailRequest request, CancellationToken ct)
    {
        // "" (what the frontend sends when there is no mailbox) and a missing value both mean "no mailbox given".
        Guid? mailboxId = null;
        if (!string.IsNullOrWhiteSpace(request.MailboxId))
        {
            if (!Guid.TryParse(request.MailboxId, out var parsed))
            {
                return BadRequest(Fail("mailboxId must be a valid GUID, or empty."));
            }

            mailboxId = parsed;
        }

        // Explicit "type" wins. Otherwise: a no-reply sender address means an automatic notice.
        var kind = request.Type ?? (LooksLikeNoReply(request.FromEmail)
            ? TicketEmailKind.Notice
            : TicketEmailKind.Conversation);

        try
        {
            var result = await _ticketEmail.SendAsync(new TicketEmailRequest(
                TicketNumber: request.MessageTicketId,
                Kind: kind,
                ToEmail: request.ToEmail,
                ToName: request.ToName,
                Subject: request.Subject,
                Html: request.Html,
                FromEmail: request.FromEmail,
                FromName: request.FromName,
                MailboxId: mailboxId), ct);

            return Ok(ApiResponse<TicketEmailResult>.Success(
                result,
                kind == TicketEmailKind.Notice ? "Notice sent." : "Message sent to the customer."));
        }
        catch (EmailSendException ex)
        {
            // Not the caller's fault: the email provider refused the message. (Rule violations - unknown ticket,
            // wrong recipient, bad sender - are NotFoundException / DomainException and are turned into
            // 404 / 400 by your ExceptionHandlingMiddleware.)
            _logger.LogError(ex, "Resend refused the email for ticket {Ticket}", request.MessageTicketId);
            return StatusCode(502, Fail("The email provider could not send the message.", ex.Message));
        }
    }

    private static bool LooksLikeNoReply(string? fromEmail) =>
        !string.IsNullOrWhiteSpace(fromEmail) &&
        (fromEmail.TrimStart().StartsWith("no-reply", StringComparison.OrdinalIgnoreCase) ||
         fromEmail.TrimStart().StartsWith("noreply", StringComparison.OrdinalIgnoreCase));

    /// <summary>Same JSON shape as your ApiResponse (succeeded / message / errors / data).</summary>
    private static object Fail(string message, params string[] errors) =>
        new { succeeded = false, message, errors, data = (object?)null };
}