using DynamicModules.Application.Common;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.Auth;

/// <summary>
/// 寫權杖的最後使用時間與 IP（追蹤式寫入，mcp1a 計畫判斷 8）。不是系統管理或設計器寫入，不在 <c>AuditCoverageTests</c> 的範圍、不寫稽核。
/// 交易內再看一次時間：並行的兩次認證只會有一次真的寫。
/// </summary>
public sealed record TouchMcpTokenRequest(long TokenId, string? ClientIp) : IRequest<Unit>, ITransactionalRequest;

public sealed class TouchMcpTokenHandler(IMcpTokenRepository tokens, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<TouchMcpTokenRequest, Unit>
{
    public async Task<Unit> Handle(TouchMcpTokenRequest request, CancellationToken ct)
    {
        var token = await tokens.GetTrackedAsync(request.TokenId, ct);
        var now = time.GetUtcNow().UtcDateTime;
        if (token is null || (token.LastUsedUtc is { } last && last >= now.AddSeconds(-McpLimits.LastUsedWriteSeconds))) return Unit.Value;
        token.LastUsedUtc = now;
        token.LastUsedIp = request.ClientIp is { Length: > 45 } ip ? ip[..45] : request.ClientIp;
        await uow.CommitAsync(ct);
        return Unit.Value;
    }
}
