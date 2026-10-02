using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Domain.Exceptions;
using System.Security.Cryptography;
using System.Text;

namespace POSShopTicketing.Application.Auth.Commands.ConfirmEmailOtp;

public class ConfirmEmailOtpCommandHandler
    : IRequestHandler<
        ConfirmEmailOtpCommand,
        string>
{
    private readonly IApplicationDbContext _context;

    public ConfirmEmailOtpCommandHandler(
        IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<string> Handle(
        ConfirmEmailOtpCommand request,
        CancellationToken cancellationToken)
    {
        var email =
            request.Email
                .Trim()
                .ToLowerInvariant();

        var hash =
            Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        request.Otp)));

        var otp = await _context.EmailVerificationOtps
        .Where(x =>
            x.Email == email &&
            x.CodeHash == hash &&
            !x.IsUsed)
        .OrderByDescending(x => x.ExpiresAt)
        .FirstOrDefaultAsync(cancellationToken);

        if (otp == null)
        {
            throw new DomainException("Invalid OTP.");
        }

        if (otp.ExpiresAt < DateTime.UtcNow)
        {
            throw new DomainException("OTP expired.");
        }

        otp.IsUsed = true;

        otp.VerificationToken =
            Guid.NewGuid().ToString();

        await _context.SaveChangesAsync(
            cancellationToken);

        return otp.VerificationToken;
    }
}