using MediatR;

namespace POSShopTicketing.Application.Auth.Queries.CheckWorkspaceAlias;

public record CheckWorkspaceAliasQuery(
    string Alias)
    : IRequest<bool>;