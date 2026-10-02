using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Domain.Exceptions;
using System.Security.Cryptography;
using System.Text;

namespace POSShopTicketing.Application.Auth.Commands.VerifyEmailWithOtp;

public class VerifyEmailWithOtpCommandHandler
    : IRequestHandler<VerifyEmailWithOtpCommand>
{
    private readonly IApplicationDbContext _context;
    private readonly IEmailSender _emailSender;

    public VerifyEmailWithOtpCommandHandler(
        IApplicationDbContext context,
        IEmailSender emailSender)
    {
        _context = context;
        _emailSender = emailSender;
    }

    public async Task Handle(
        VerifyEmailWithOtpCommand request,
        CancellationToken cancellationToken)
    {
        var email =
            request.Email
                .Trim()
                .ToLowerInvariant();

        var exists =
            await _context.TeamMembers
                .AsNoTracking()
                .AnyAsync(
                    x => x.Email == email,
                    cancellationToken);

        if (exists)
        {
            throw new DomainException(
                $"Email '{email}' already exists.");
        }

        var otp =
            RandomNumberGenerator
                .GetInt32(100000, 999999)
                .ToString();

        var hash =
            Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(otp)));

        var entity =
            new EmailVerificationOtp
            {
                Email = email,
                CodeHash = hash,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10),
                IsUsed = false
            };

        _context.EmailVerificationOtps.Add(entity);

        await _context.SaveChangesAsync(
            cancellationToken);

        var bodyHtml = $@"
<div style='font-family:Arial,sans-serif'>

<h2>RouteBoard Email Verification</h2>

<p>
Use the verification code below:
</p>

<h1>{otp}</h1>

<p>
Code expires in 10 minutes.
</p>

</div>";

        await _emailSender.SendAsync(
            email,
            email,
            "RouteBoard Email Verification",
            bodyHtml,
            cancellationToken);
    }
}