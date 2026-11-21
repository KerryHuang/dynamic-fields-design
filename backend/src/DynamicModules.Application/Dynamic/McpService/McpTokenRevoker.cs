using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.McpService;

/// <summary>
/// 帳號連動撤銷（設計 §13.2、U4；mcp1a 計畫判斷 23）：停用帳號、管理員重設密碼時撤銷這個人全部有效的權杖。
/// 追蹤式寫入，不自己存檔：由呼叫端 handler 的 <c>CommitAsync</c> 與帳號異動在同一交易存檔。已撤銷、已過期的不動。
/// </summary>
public sealed class McpTokenRevoker(IMcpTokenRepository tokens, TimeProvider time)
{
    /// <summary>回傳這次撤銷的筆數。</summary>
    public async Task<int> RevokeAllAsync(string userId, McpRevokeReason reason, string revokedBy, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var active = await tokens.ListActiveTrackedAsync(userId, now, ct);
        foreach (var t in active)
        {
            t.RevokedUtc = now;
            t.RevokedBy = revokedBy;
            t.RevokeReason = reason;
        }
        return active.Count;
    }
}
