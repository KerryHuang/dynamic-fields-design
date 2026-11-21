using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.Tools;

/// <summary><c>whoami</c> 的資料（設計 U5、計畫判斷 20）：工具類別只收 <c>ISender</c>，組合資料寫在這個 request。只在 MCP 通道有意義。</summary>
public sealed record GetMcpWhoAmIRequest : IRequest<ServiceResult>;

public sealed record McpWhoAmIToken(long Id, string Name, IReadOnlyList<string> Scopes, DateTime ExpiresAt);

public sealed record McpWhoAmILimits(int MaxRows, int CallsPerMinute, int CallsPerDay, int MaxResultChars);

public sealed record McpWhoAmIResponse(string UserId, string? DisplayName, McpWhoAmIToken Token, McpWhoAmILimits Limits, string ServerVersion);

public sealed class GetMcpWhoAmIHandler(
    ICurrentUser current, IRequestChannel channel, IMcpTokenRepository tokens, IAccountRepository accounts, McpPolicies policies,
    IOpsEnvironment environment) : IRequestHandler<GetMcpWhoAmIRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetMcpWhoAmIRequest request, CancellationToken ct)
    {
        if (channel.Channel != RequestChannel.Mcp || channel.TokenId is not { } tokenId) return ServiceResult.Forbidden();
        var token = await tokens.GetTrackedAsync(tokenId, ct);
        if (token is null || token.UserId != current.UserId) return ServiceResult.Forbidden();
        var names = await accounts.GetDisplayNamesAsync([current.UserId], ct);
        var snapshot = await policies.ReadAsync(ct);
        return ServiceResult.Success(new McpWhoAmIResponse(current.UserId, names.GetValueOrDefault(current.UserId),
            new McpWhoAmIToken(token.Id, token.Name, channel.Scopes, token.ExpiresUtc),
            new McpWhoAmILimits(snapshot.MaxRows, snapshot.CallsPerMinute, snapshot.CallsPerDay, McpLimits.MaxResultChars),
            environment.Version));
    }
}
