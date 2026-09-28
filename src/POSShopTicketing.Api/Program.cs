using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using POSShopTicketing.Api.Middleware;
using POSShopTicketing.Application;
using POSShopTicketing.Application.Auth.Common;
using POSShopTicketing.Infrastructure;
using POSShopTicketing.Infrastructure.BackgroundJobs;
using POSShopTicketing.Infrastructure.Identity;
using POSShopTicketing.Infrastructure.Persistence;
using Serilog;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

// ---- Serilog: one log file per run -------------------------------------
//
// runStartedAt is captured once, here, before anything else - it's what
// makes the file sink below distinct per process start rather than a
// single ever-growing (or merely per-day) file: every time this app
// starts, "logs/posshopticketing-{that timestamp}.log" is a brand-new
// file holding exactly that run's events, from this line to shutdown.
var runStartedAt = DateTime.Now;
var logFilePath = Path.Combine("logs", $"posshopticketing-{runStartedAt:yyyyMMdd-HHmmss}.log");

// Bootstrap logger: active immediately, before configuration or DI exist,
// so failures during host startup itself are still captured somewhere
// instead of disappearing silently. Replaced by the fully-configured
// logger (reading appsettings.json, enrichers, both sinks) a few lines
// down, once the host is built.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting POSShopTicketing API host (run started at {RunStartedAt})", runStartedAt);

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .Enrich.WithProperty("RunStartedAt", runStartedAt)
        .WriteTo.Console(
            outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext}{NewLine}      {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            logFilePath,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} (Thread {ThreadId}){NewLine}      {Message:lj}{NewLine}{Exception}",
            // No rollingInterval here - deliberately one continuous file
            // for the life of this run. fileSizeLimitBytes/rollOnFileSizeLimit
            // still protect disk space if a single run goes on for a very
            // long time (a long-lived deployment rather than a dev `dotnet run`).
            fileSizeLimitBytes: 100 * 1024 * 1024,
            rollOnFileSizeLimit: true,
            retainedFileCountLimit: 31));

    // ---- Services -------------------------------------------------------

    builder.Services
        .AddControllers(options =>
        {
            // A token is required on every request by default - /auth/* and
            // /webhooks/* opt out with [AllowAnonymous].
            options.Filters.Add(new AuthorizeFilter());
        })
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "POSShopTicketing API",
            Version = "v1",
            Description = "Multi-tenant helpdesk backend for service providers: omnichannel email-to-ticket " +
                          "conversion with Message-ID threading, unregistered-sender triage, role-based " +
                          "assignment routing, SLA tracking, and JWT auth with tenant isolation " +
                          "(EF Core global filters + PostgreSQL Row-Level Security)."
        });

        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "Bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Paste the accessToken from /api/auth/login (no \"Bearer \" prefix needed here)."
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
                Array.Empty<string>()
            }
        });

        options.SupportNonNullableReferenceTypes();
    });

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    // ---- Auth: JWT bearer, required on every request by default -----------

    var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
        ?? throw new InvalidOperationException("Jwt configuration section is missing.");

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            // Keep claim types exactly as issued ("sub", "tenant_id") rather
            // than remapped to legacy long-form URIs.
            options.MapInboundClaims = false;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtSettings.Issuer,
                ValidateAudience = true,
                ValidAudience = jwtSettings.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Secret)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            };
        });

    builder.Services.AddAuthorization();

    // ---- Rate limiting: the inbound webhook is the one public,
    // unauthenticated surface (spec's security notes), so it gets its own
    // tighter limit distinct from everything else behind auth. -------------

    builder.Services.AddRateLimiter(options =>
    {
        options.AddFixedWindowLimiter("webhooks", limiterOptions =>
        {
            limiterOptions.PermitLimit = 60;
            limiterOptions.Window = TimeSpan.FromMinutes(1);
            limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            limiterOptions.QueueLimit = 20;
        });
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    });

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("Default", policy =>
        {
            policy.WithOrigins(
                    "https://routeboars.com",
                    "http://localhost:4200",
                    "https://routeboard-online.netlify.app",
                    "https://routeboard.online",
                    "https://routeboard.online.com",
                    "https://www.routeboars.com")
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });

    builder.Services.AddScoped<AuthResultFactory>();

    var app = builder.Build();

    // ---- Pipeline ---------------------------------------------------------

    // Placed right after the exception middleware so it still sees and
    // logs the correct final status code even for exceptions that
    // middleware translates into a 4xx/5xx ApiResponse.
    app.UseMiddleware<ExceptionHandlingMiddleware>();
    app.UseSerilogRequestLogging();

    //if (app.Environment.IsDevelopment())
    //{
        app.UseSwagger();
    
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "swagger";
        options.SwaggerEndpoint(
            "/swagger/v1/swagger.json",
            "POSShopTicketing API v1");
    });

   // }

    app.UseHttpsRedirection();
    app.UseCors("Default");
    app.UseRateLimiter();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();

    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[] { new HangfireDashboardAuthorizationFilter() }
    });

    // Apply migrations and seed fixture data on startup in Development only.
    // In other environments, run migrations explicitly as a deployment step.
    //if (app.Environment.IsDevelopment())
    //{
    //    using var scope = app.Services.CreateScope();
    //    var initializer = scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitializer>();
    //    await initializer.InitialiseAsync();
    //    await initializer.SeedAsync();
    //}

    using (var scope = app.Services.CreateScope())
    {
        var initializer = scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitializer>();

        await initializer.InitialiseAsync();

        if (app.Environment.IsDevelopment())
        {
            await initializer.SeedAsync();
        }
    }

    RecurringJob.AddOrUpdate<SlaBreachRecurringJob>(
        "sla-breach-sweep", job => job.RunAsync(CancellationToken.None), "*/5 * * * *");

    

    Log.Information("POSShopTicketing API host built - logging this run to {LogFilePath}", logFilePath);

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // HostAbortedException is thrown by the `dotnet ef migrations`
    // tooling's design-time host build and isn't a real startup failure -
    // excluded so `dotnet ef` commands don't log a scary false alarm.
    Log.Fatal(ex, "POSShopTicketing API host terminated unexpectedly");
    throw;
}
finally
{
    Log.Information("POSShopTicketing API host shutting down (run started at {RunStartedAt})", runStartedAt);
    Log.CloseAndFlush();
}

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program { }
