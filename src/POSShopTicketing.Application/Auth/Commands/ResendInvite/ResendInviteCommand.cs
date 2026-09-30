using MediatR;

namespace POSShopTicketing.Application.Auth.Commands.ResendInvite;

public sealed record ResendInviteCommand(Guid TeamMemberId) : IRequest;