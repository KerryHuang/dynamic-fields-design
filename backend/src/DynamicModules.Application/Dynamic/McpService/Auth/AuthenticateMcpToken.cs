using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.McpService.Tokens;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.McpService.Auth;

public enum McpAuthStatus { Ok, Unauthorized, Forbidden }

/// <summary>
/// 權杖認證的結果（mcp1a 計畫判斷 8）。<see cref="McpAuthStatus.Forbidden"/> 一定帶 <see cref="UserId"/> 與 <see cref="TokenId"/>：
/// 走到政策檢查時權杖與帳號都已驗過，是可信身分，閘門用它寫 <c>mcp.access</c> 稽核。<see cref="McpAuthStatus.Unauthorized"/> 不帶任何身分。
/// </summary>
public sealed record McpAuthResult(McpAuthStatus Status, string? UserId = null, long? TokenId = null,
    IReadOnlyList<string>? Scopes = null, DateTime? ExpiresUtc = null, string? Code = null)
{
    public static readonly McpAuthResult Unauthorized = new(McpAuthStatus.Unauthorized);
}

/// <summary>以明文權杖認證；非交易、只讀（通過後另送 <see cref="TouchMcpTokenRequest"/>）。每次都重查資料庫，不快取。</summary>
public sealed record AuthenticateMcpTokenRequest(string RawToken, string? ClientIp) : IRequest<McpAuthResult>;

/// <summary>
/// 判斷順序固定（判斷 8）：形狀 → 雜湊查列並以 <see cref="McpSecrets.HashEquals"/> 比對、撤銷、過期 → 範圍可解析 → 帳號 →
/// Demo 站 → 政策（壞值、未啟用、不在允許對象）。前四步不符一律 <see cref="McpAuthStatus.Unauthorized"/>，不透露是哪一步。
/// </summary>
public sealed class AuthenticateMcpTokenHandler(
    IMcpTokenRepository tokens, IAccountRepository accounts, AuthSettings auth, McpPolicies policies, ISender sender, TimeProvider time,
    ILogger<AuthenticateMcpTokenHandler> logger)
    : IRequestHandler<AuthenticateMcpTokenRequest, McpAuthResult>
{
    public async Task<McpAuthResult> Handle(AuthenticateMcpTokenRequest request, CancellationToken ct)
    {
        if (!McpSecrets.LooksLikeToken(request.RawToken)) return McpAuthResult.Unauthorized;
        var hash = McpSecrets.Hash(request.RawToken);
        var token = await tokens.FindByHashAsync(hash, ct);
        var now = time.GetUtcNow().UtcDateTime;
        if (token is null || !McpSecrets.HashEquals(hash, token.TokenHash) || token.RevokedUtc is not null || token.ExpiresUtc <= now)
            return McpAuthResult.Unauthorized;
        if (!McpScopes.TryParse(token.Scopes, out var scopes)) return McpAuthResult.Unauthorized;
        if (!McpTokenViews.AccountUsable(await accounts.GetTrackedAsync(token.UserId, ct))) return McpAuthResult.Unauthorized;

        if (auth.DemoLoginEnabled) return Forbidden(token.UserId, token.Id, McpMessages.CodeDemoSite);
        var snapshot = await policies.ReadAsync(ct);
        if (await policies.AccessDenialAsync(snapshot, token.UserId, ct) is { } denial) return Forbidden(token.UserId, token.Id, denial);

        if (token.LastUsedUtc is not { } last || last < now.AddSeconds(-McpLimits.LastUsedWriteSeconds))
        {
            // 最後使用時間只是參考資訊：寫不進去記 Warning，不擋這次呼叫
            try
            {
                await sender.Send(new TouchMcpTokenRequest(token.Id, request.ClientIp), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "MCP 權杖 {TokenId} 的最後使用時間寫入失敗", token.Id);
            }
        }
        return new McpAuthResult(McpAuthStatus.Ok, token.UserId, token.Id, scopes, token.ExpiresUtc);
    }

    private static McpAuthResult Forbidden(string userId, long tokenId, string code) =>
        new(McpAuthStatus.Forbidden, userId, tokenId, Code: code);
}
