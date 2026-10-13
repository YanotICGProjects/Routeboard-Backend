using System.Net.Mail;
using System.Security.Cryptography;
using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Exceptions;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Domain.Enums;
using POSShopTicketing.Domain.Exceptions;

namespace POSShopTicketing.Application.Mailboxes.Commands.CreateMailbox;

public static class MailboxSecrets
{
    /// <summary>Hex, not Base64: Base64 contains + / = which break URLs.</summary>
    public static string Generate() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}

/// <summary>
/// Two ways to get a working inbound address:
///   - Omit EmailAddress -> system address {slug}@support.routeboard.online, verified immediately.
///   - Supply EmailAddress -> a tenant-owned address (support@theirdomain.com). The tenant sets
///     up forwarding to their SYSTEM address; mail then arrives with the system address as the
///     envelope recipient, which is how the webhook finds the workspace.
/// </summary>
public record CreateMailboxCommand : IRequest<Guid>
{
    public string? EmailAddress { get; init; }
    public MailboxProvider Provider { get; init; } = MailboxProvider.SendGridInboundParse;
    public bool IsDefault { get; init; }
}

public class CreateMailboxCommandHandler : IRequestHandler<CreateMailboxCommand, Guid>
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly ISystemMailboxAddressProvider _systemMailboxAddressProvider;

    public CreateMailboxCommandHandler(
        IApplicationDbContext context,
        ICurrentTenantService currentTenantService,
        ISystemMailboxAddressProvider systemMailboxAddressProvider)
    {
        _context = context;
        _currentTenantService = currentTenantService;
        _systemMailboxAddressProvider = systemMailboxAddressProvider;
    }

    public async Task<Guid> Handle(CreateMailboxCommand request, CancellationToken ct)
    {
        var tenantId = _currentTenantService.TenantId
            ?? throw new ForbiddenException("Mailboxes must be created from within a tenant.");

        var isSystemProvided = string.IsNullOrWhiteSpace(request.EmailAddress);

        string address;
        if (isSystemProvided)
        {
            var tenant = await _context.Tenants.FindAsync(new object[] { tenantId }, ct)
                ?? throw new NotFoundException(nameof(Tenant), tenantId);

            address = !string.IsNullOrWhiteSpace(tenant.SupportEmail)
    ? tenant.SupportEmail.Trim().ToLowerInvariant()
    : _systemMailboxAddressProvider.BuildAddress(tenant.WorkspaceAlias);
        }
        else
        {
            address = request.EmailAddress!.Trim().ToLowerInvariant();

            if (!MailAddress.TryCreate(address, out var parsed) || parsed.Address != address)
            {
                throw new DomainException("Please enter a valid email address.");
            }

            if (_systemMailboxAddressProvider.IsSystemDomain(address))
            {
                throw new DomainException(
                    "That address is on the platform's own domain - omit EmailAddress entirely " +
                    "to get a zero-setup system-provided mailbox.");
            }
        }

        if (await _context.Mailboxes.AsNoTracking().AnyAsync(m => m.EmailAddress == address, ct))
        {
            throw new DomainException($"Mailbox \"{address}\" is already registered.");
        }

        // The tenant's first mailbox is always the default.
        var hasAny = await _context.Mailboxes.AnyAsync(m => m.TenantId == tenantId, ct);
        var makeDefault = request.IsDefault || !hasAny;

        if (makeDefault)
        {
            var currentDefaults = await _context.Mailboxes
                .Where(m => m.TenantId == tenantId && m.IsDefault)
                .ToListAsync(ct);

            foreach (var m in currentDefaults) m.IsDefault = false;
        }

        var entity = new Mailbox
        {
            TenantId = tenantId,
            EmailAddress = address,
            Provider = request.Provider,
            // No longer used to authenticate the webhook (that's one platform secret now);
            // kept so the column/DTOs don't have to change.
            WebhookSecret = MailboxSecrets.Generate(),
            IsVerified = isSystemProvided,
            IsSystemProvided = isSystemProvided,
            IsDefault = makeDefault
        };

        _context.Mailboxes.Add(entity);

        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two requests racing for the same address: the unique index on EmailAddress caught it.
            if (await _context.Mailboxes.AsNoTracking().AnyAsync(m => m.EmailAddress == address, ct))
            {
                throw new DomainException($"Mailbox \"{address}\" is already registered.");
            }
            throw;
        }

        return entity.Id;
    }
}

/* =====================================================================
   WIRING - paste these into the places named. Not one compilable file.
   =====================================================================

   ---- A. RegisterTenantCommandHandler: the missing link --------------
   Your handler stores tenant.SupportEmail but never creates the Mailbox
   ROW, so mail to alias@support.routeboard.online would find nothing.

   1) Inject ISystemMailboxAddressProvider.
   2) Validate/normalize the alias at the very top of Handle():

        if (!WorkspaceAliasRules.TryNormalize(request.WorkspaceAlias, out var alias, out var aliasError))
            throw new DomainException(aliasError!);

        var workspaceEmail = _systemMailboxAddressProvider.BuildAddress(alias);   // no hard-coded domain
        // ...use `alias` instead of request.WorkspaceAlias everywhere below.

   3) Right after _currentTenantService.SetTenant(tenant.Id):

        _context.Mailboxes.Add(new Mailbox
        {
            TenantId = tenant.Id,
            EmailAddress = workspaceEmail,
            Provider = MailboxProvider.SendGridInboundParse,   // or your Postmark value
            WebhookSecret = MailboxSecrets.Generate(),
            IsVerified = true,
            IsSystemProvided = true,
            IsDefault = true
        });

   ---- B. Program.cs ---------------------------------------------------
        builder.Services.Configure<InboundEmailOptions>(
            builder.Configuration.GetSection("InboundEmail"));

   appsettings / user-secrets / env var (NEVER commit the real value):
        "InboundEmail": { "WebhookSecret": "<long random string>" }
        // generate one:  openssl rand -hex 32

   Keep your existing "webhooks" rate-limit policy.

   ---- C. EF Core configuration (then add a migration) ----------------
        // TicketMessage: stops two simultaneous retries inserting the same email twice
        builder.HasIndex(m => new { m.TenantId, m.MessageId })
               .IsUnique()
               .HasFilter("\"MessageId\" IS NOT NULL");      // PostgreSQL syntax

        // Mailbox: one owner per address, globally
        builder.HasIndex(m => m.EmailAddress).IsUnique();

   ---- D. Provider dashboard (one time) --------------------------------
        DNS:  MX record for support.routeboard.online -> provider's value
        SendGrid: Inbound Parse -> host support.routeboard.online
                  URL https://api.routeboard.online/api/webhooks/inbound-email/sendgrid?secret=<SECRET>
        Postmark: Inbound webhook URL
                  https://rb:<SECRET>@api.routeboard.online/api/webhooks/inbound-email/postmark
   ===================================================================== */