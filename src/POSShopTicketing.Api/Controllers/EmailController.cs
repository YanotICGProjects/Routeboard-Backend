using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using POSShopTicketing.Api.Models.Email;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Domain.Entities;           // TicketEmailKind (as in your current file)
using POSShopTicketing.Infrastructure.Email;      // EmailSendException
using POSShopTicketing.Shared.Wrappers;

namespace POSShopTicketing.Api.Controllers;

/// <summary>
/// REPLACES Controllers/EmailController.cs (the whole file).
///
///   POST /api/email/send
///        The one endpoint that emails a customer about a ticket. It never creates a ticket.
///          Notice        automatic no-reply message (never part of the thread)
///          Conversation  an agent's message, from the workspace support address, in the ticket's one mail thread.
///                        The first one on a ticket without a thread STARTS the thread; the response returns threadId.
///
///   GET  /api/email/thread/{ticketNumber}
///        The ticket's threadId (null until the first message is emailed), the address the next conversation email
///        comes from, and the subject it will carry - for the UI to show before the agent writes.
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
        // Explicit "type" wins. Otherwise: a no-reply sender address means an automatic notice.
        var kind = request.Type ?? (LooksLikeNoReply(request.FromEmail)
            ? TicketEmailKind.Notice
            : TicketEmailKind.Conversation);

        try
        {
            var result = await _ticketEmail.SendAsync(new TicketEmailRequest(
                TicketNumber: request.MessageTicketNumber,
                Kind: kind,
                ToEmail: request.ToEmail,
                ToName: request.ToName,
                Subject: request.Subject,
                Html: request.Html,
                FromEmail: request.FromEmail,
                FromName: request.FromName,
                ThreadId: request.ThreadId), ct);

            return Ok(ApiResponse<TicketEmailResult>.Success(
                result,
                kind == TicketEmailKind.Notice ? "Notice sent." : "Message sent to the customer."));
        }
        catch (EmailSendException ex)
        {
            // Not the caller's fault: the email provider refused the message. (Rule violations - unknown ticket,
            // wrong recipient, bad sender or thread - are NotFoundException / DomainException and are turned into
            // 404 / 400 by your ExceptionHandlingMiddleware.)
            _logger.LogError(ex, "Resend refused the email for ticket {Ticket}", request.MessageTicketNumber);
            return StatusCode(502, Fail("The email provider could not send the message.", ex.Message));
        }
    }

    //[HttpGet("thread/{ticketNumber}")]
    //public async Task<IActionResult> GetThread(string ticketNumber, CancellationToken ct)
    //{
    //    var info = await _ticketEmail.GetThreadAsync(ticketNumber, ct);
    //    return Ok(ApiResponse<TicketThreadInfo>.Success(info));
    //}

    private static bool LooksLikeNoReply(string? fromEmail) =>
        !string.IsNullOrWhiteSpace(fromEmail) &&
        (fromEmail.TrimStart().StartsWith("no-reply", StringComparison.OrdinalIgnoreCase) ||
         fromEmail.TrimStart().StartsWith("noreply", StringComparison.OrdinalIgnoreCase));

    /// <summary>Same JSON shape as your ApiResponse (succeeded / message / errors / data).</summary>
    private static object Fail(string message, params string[] errors) =>
        new { succeeded = false, message, errors, data = (object?)null };
}