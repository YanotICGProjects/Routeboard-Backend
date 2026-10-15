using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Application.Tickets.Commands.AddTicketMessage;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Infrastructure.Persistence;

namespace POSShopTicketing.Infrastructure.BackgroundJobs;

/// <summary>
/// REPLACES Infrastructure/BackgroundJobs/SendReplyEmailJob.cs (the whole file).
///
/// Hangfire-invoked: sends an agent's reply in the background so the request stays fast, and
/// Hangfire retries on failure.
///
/// WHAT WAS WRONG BEFORE
/// The old code set the Reply-To to  ticket+{ticketId}@mail.posshopticketing.app.
/// That domain has no MX record pointing at your email provider, so when a customer hit
/// "Reply", their answer went nowhere and never became a message on the ticket.
///
/// WHAT IT DOES NOW
/// The Reply-To is the workspace address the ticket arrived on (for example
/// acme@support.routeboard.online). That address IS on your catch-all domain, so the customer's
/// reply goes: customer -> Resend MX -> your webhook -> inbound handler, which finds the workspace
/// from that address and threads the message into the ticket (In-Reply-To header, or the
/// [TICKET-NUMBER] in the subject as a fallback).
/// </summary>
public class SendReplyEmailJob : ISendReplyEmailJob
{
    private readonly ApplicationDbContext _context;
    private readonly IEmailSender _emailSender;

    public SendReplyEmailJob(ApplicationDbContext context, IEmailSender emailSender)
    {
        _context = context;
        _emailSender = emailSender;
    }

    public async Task SendAsync(
        Guid ticketMessageId, string toEmail, string? toName, string subject, string bodyHtml,
        string? inReplyToMessageId, string generatedMessageId, CancellationToken cancellationToken)
    {
        var message = await _context.TicketMessages
            .IgnoreQueryFilters()
            .Include(m => m.Ticket)
            .FirstOrDefaultAsync(m => m.Id == ticketMessageId, cancellationToken);

        if (message?.Ticket is null)
        {
            return;
        }

        var replyAddress = await ResolveReplyAddressAsync(message.Ticket, cancellationToken);

        var tenantName = await _context.Tenants
    .IgnoreQueryFilters()
    .Where(x => x.Id == message.Ticket.TenantId)
    .Select(x => x.Name)
    .FirstOrDefaultAsync(cancellationToken);

        await _emailSender.SendReplyAsync(
    new OutboundEmailMessage(
        ToEmail: toEmail,
        ToName: toName,
        FromEmail: replyAddress,
        FromName: tenantName,
        FromAlias: replyAddress,
        Subject: subject,
        BodyHtml: bodyHtml,
        InReplyToMessageId: inReplyToMessageId,
        GeneratedMessageId: generatedMessageId),
    cancellationToken);
    }

    /// <summary>
    /// Which address should the customer's reply go to? Tried in this order:
    ///   1. the mailbox this ticket originally arrived on
    ///   2. the workspace's default verified mailbox   (tickets created manually have no mailbox)
    ///   3. the workspace's own support address
    /// </summary>
    private async Task<string> ResolveReplyAddressAsync(Ticket ticket, CancellationToken ct)
    {
        var mailboxId = ticket.MailboxId;

        var arrivedOn = await _context.Mailboxes
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.Id == mailboxId)
            .Select(m => m.EmailAddress)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(arrivedOn)) return arrivedOn;

        var defaultMailbox = await _context.Mailboxes
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.TenantId == ticket.TenantId && m.IsVerified && m.IsDefault)
            .Select(m => m.EmailAddress)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(defaultMailbox)) return defaultMailbox;

        var supportEmail = await _context.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.Id == ticket.TenantId)
            .Select(t => t.SupportEmail)
            .FirstOrDefaultAsync(ct);

        return !string.IsNullOrWhiteSpace(supportEmail)
            ? supportEmail
            : throw new InvalidOperationException(
                $"No reply address found for ticket {ticket.Id}: it has no mailbox and the tenant has no support email.");
    }
}