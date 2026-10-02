using MediatR;

namespace POSShopTicketing.Application.Auth.Commands.ConfirmEmailOtp;

public record ConfirmEmailOtpCommand(string Email, string Otp)  : IRequest<string>;