using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;          // SendEmailRequest
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Infrastructure.Email;      // IOutboundEmailSender, OutboundEmail, EmailProviderInfo

namespace POSShopTicketing.Api.Controllers;

// REPLACES Controllers/EmailController.cs (the whole file).
// Every endpoint is Development-only. Without that guard, anyone on the internet could
// use these to send email from your accounts.
[ApiController]
[Route("api/email")]
public class EmailController : ControllerBase
{
    private readonly IEmailSender _appEmailSender;        // the router: what the app REALLY uses
    private readonly IOutboundEmailSender _resend;        // always Resend, for testing the Resend key
    private readonly EmailProviderInfo _routing;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<EmailController> _logger;

    public EmailController(
        IEmailSender appEmailSender,
        IOutboundEmailSender resend,
        EmailProviderInfo routing,
        IWebHostEnvironment env,
        ILogger<EmailController> logger)
    {
        _appEmailSender = appEmailSender;
        _resend = resend;
        _routing = routing;
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// THE CHECK:  GET /api/email/provider
    /// With the default settings you should see Smtp for both. If you ever see Resend for
    /// "transactional" when you didn't mean to, the Email:TransactionalProvider setting says so.
    /// </summary>
    [HttpGet("provider")]
    public IActionResult Provider()
    {
        if (!_env.IsDevelopment()) return NotFound();

        return Ok(new
        {
            environment = _env.EnvironmentName,
            transactional = _routing.Transactional.ToString(),   // invites, OTPs, audit emails
            ticketReply = _routing.TicketReply.ToString(),       // replies on a ticket thread
            emailSender = _appEmailSender.GetType().Name         // always "RoutingEmailSender"
        });
    }

    /// <summary>Sends through the app's normal path (your Gmail SMTP with default settings).</summary>
    [HttpPost("send-app")]
    public async Task<IActionResult> SendViaApp([FromBody] SendEmailRequest request, CancellationToken ct)
    {
        if (!_env.IsDevelopment()) return NotFound();

        await _appEmailSender.SendAsync(request.ToEmail, request.ToName, request.Subject, request.Html, ct);
        return Ok(new { sentWith = _routing.Transactional.ToString() });
    }

    /// <summary>Sends straight through Resend, to check the Resend key and domain. Shows the real error on failure.</summary>
    [HttpPost("send")]
    public async Task<IActionResult> SendViaResend([FromBody] SendEmailRequest request, CancellationToken ct)
    {
        if (!_env.IsDevelopment()) return NotFound();

        try
        {
            var id = await _resend.SendAsync(
                new OutboundEmail(request.ToEmail, request.ToName, request.Subject, request.Html), ct);
            return Ok(new { success = true, resendId = id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resend test send failed");
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }
}