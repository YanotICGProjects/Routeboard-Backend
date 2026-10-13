using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Infrastructure.BackgroundJobs;
using POSShopTicketing.Infrastructure.Identity;
using POSShopTicketing.Infrastructure.Persistence;
using POSShopTicketing.Infrastructure.Persistence.Interceptors;
using POSShopTicketing.Infrastructure.Services;
using POSShopTicketing.Infrastructure.Storage;

namespace POSShopTicketing.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not found. Set it in appsettings.json or user-secrets.");

        services.AddScoped<AuditableEntitySaveChangesInterceptor>();
        services.AddScoped<TenantSessionInterceptor>();

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName));
        });

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<ApplicationDbContextInitializer>();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<ICurrentTenantService, CurrentTenantService>();
        services.AddSingleton<IDateTime, DateTimeService>();
        services.AddScoped<ITicketNumberGenerator, TicketNumberGenerator>();

        // Core ticketing features: automated workflows
        services.AddScoped<ITicketAssignmentService, TicketAssignmentService>();
        services.AddScoped<ITicketPriorityClassifier, TicketPriorityClassifier>();
        services.AddScoped<IAlertNotifier, AlertNotifier>();
        services.AddScoped<IHtmlSanitizerService, HtmlSanitizerService>();
        services.Configure<AttachmentStorageOptions>(
    configuration.GetSection("Attachments"));

        services.AddScoped<IAttachmentStorage,
            LocalDiskAttachmentStorage>();

        // Outbound email: any SMTP-capable provider once Smtp:Host is
        // set (Hostinger's own mail hosting, Gmail, Amazon SES, Mailgun,
        // Postmark, or SendGrid's own SMTP relay) - falls back to the
        // safe, zero-config log-based sender otherwise.
        services.Configure<SmtpSettings>(configuration.GetSection(SmtpSettings.SectionName));
        var smtpHost = configuration.GetSection(SmtpSettings.SectionName)["Host"];
        if (!string.IsNullOrWhiteSpace(smtpHost))
        {
            services.AddScoped<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            services.AddScoped<IEmailSender, EmailSender>();
        }

        // Activity/audit trail: every login, logout, and ticket
        // operation - persisted to AuditLog always, and emailed to the
        // tenant's active Owners/Admins whenever AuditEmail:Enabled
        // (default true) and outbound email is actually configured
        // above.
        services.Configure<AuditEmailSettings>(configuration.GetSection(AuditEmailSettings.SectionName));
        services.AddScoped<IAuditTrailService, AuditTrailService>();

        // Zero-setup mailboxes: {tenant.Slug}@{InboundEmail:SystemDomain}
        services.Configure<InboundEmailSettings>(configuration.GetSection(InboundEmailSettings.SectionName));
        services.AddScoped<ISystemMailboxAddressProvider, SystemMailboxAddressProvider>();

        // Auth: signup/login, JWT issuance, refresh tokens
        services.Configure<JwtSettings>(configuration.GetSection(JwtSettings.SectionName));
        services.AddScoped<IPasswordHasherService, PasswordHasherService>();
        services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();

        // Links this backend puts into emails (currently just the
        // invite-accept link), pointing at a frontend it doesn't serve
        // itself - see InviteTeamMemberCommand.
        services.Configure<AppUrlSettings>(configuration.GetSection(AppUrlSettings.SectionName));
        services.AddScoped<IAppUrlProvider, AppUrlProvider>();

        // Background jobs (Hangfire, Postgres-backed) - inbound webhook
        // processing stays synchronous for correctness (the caller needs
        // the resulting ticket id back), but the outbound send queue and
        // the SLA sweep both run here per the spec's architecture.
        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(connectionString)));

        services.AddHangfireServer();

        services.AddScoped<IBackgroundJobScheduler, BackgroundJobScheduler>();
        services.AddScoped<POSShopTicketing.Application.Tickets.Commands.AddTicketMessage.ISendReplyEmailJob, SendReplyEmailJob>();
        services.AddScoped<SlaBreachRecurringJob>();

        return services;
    }
}
