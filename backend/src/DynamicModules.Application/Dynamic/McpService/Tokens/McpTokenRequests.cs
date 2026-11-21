using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.Tokens;

// 個人存取權杖 API（mcp1a 計畫判斷 7）。本人路徑 me/mcp/*，管理員路徑 system/mcp/*；MCP 關閉時只有建立被擋，列出與撤銷照常可用。

/// <summary>本人的 AI 連線資訊；<paramref name="ServerBaseUrl"/> 由 controller 給（<c>Ops:PublicUrl</c>，沒設時是請求的 scheme＋host，判斷 15）。</summary>
public sealed record GetMyMcpInfoRequest(string ServerBaseUrl) : IRequest<ServiceResult>;

public sealed record ListMyMcpTokensRequest : IRequest<ServiceResult>;

/// <summary>建立權杖；<paramref name="Password"/> 是重新輸入的登入密碼，與登入同一套降速（判斷 16），不寫進稽核。</summary>
public sealed record CreateMcpTokenRequest(string? Name, IReadOnlyList<string>? Scopes, DateOnly? ExpiresOn, string? Password, string? ClientIp)
    : IRequest<ServiceResult>, ITransactionalRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("mcp.token.create", AuditTargets.User, null);
}

public sealed record RevokeMyMcpTokenRequest(long Id) : IRequest<ServiceResult>, ITransactionalRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("mcp.token.revoke", AuditTargets.User, null);
}

public sealed record GetMyMcpTokenCallsRequest(long Id) : IRequest<ServiceResult>;

/// <summary>管理員列表；<paramref name="Status"/> 是 <c>active</c>／<c>expired</c>／<c>revoked</c>，空白列全部。</summary>
public sealed record AdminListMcpTokensRequest(string? UserId, string? Status) : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed record AdminRevokeMcpTokenRequest(long Id) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("mcp.token.revoke", AuditTargets.User, null);
}

public sealed record AdminRevokeAllMcpTokensRequest(string UserId)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("mcp.token.revokeAll", AuditTargets.User, UserId);
}
