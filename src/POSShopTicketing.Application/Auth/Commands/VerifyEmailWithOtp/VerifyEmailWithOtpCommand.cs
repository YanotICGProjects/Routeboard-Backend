using MediatR;

namespace POSShopTicketing.Application.Auth.Commands.VerifyEmailWithOtp;

public record VerifyEmailWithOtpCommand(string Email)  : IRequest;