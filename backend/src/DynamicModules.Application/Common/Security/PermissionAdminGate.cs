using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Common.Security;

/// <summary>權限設定檢查的結果；McpChannel 由 mcp1a 加入請求通道後才會出現（權限軸設計 §7.1、M8）。</summary>
public enum PermissionAdminCheck { Allowed, NotPermissionAdmin, McpChannel }

/// <summary>
/// 「權限設定」的唯一檢查點與 Authorized 寫入許可唯一的開啟處（權限軸設計 §7.1、§7.4，T6）：
/// <see cref="Behaviors.PermissionAdminBehavior{TReq,TRes}"/> 與只有某些請求內容才算授權的 handler 都經它，不在 handler 各自讀權限表。
/// </summary>
public sealed class PermissionAdminGate(
    IDynamicAccessGuard guard, ICurrentUser current, IAccountRepository accounts, IAuthorizationChangeProbe changes, IRequestChannel channel)
{
    /// <summary>只做判斷、不開許可。MCP 通道一律 <see cref="PermissionAdminCheck.McpChannel"/>，不看身分（mcp1a 計畫判斷 9、PAX-22）。</summary>
    public async Task<PermissionAdminCheck> CheckAsync(CancellationToken ct)
    {
        if (channel.Channel == RequestChannel.Mcp) return PermissionAdminCheck.McpChannel;
        return await guard.IsPermissionAdminAsync(ct) ? PermissionAdminCheck.Allowed : PermissionAdminCheck.NotPermissionAdmin;
    }

    /// <summary>檢查結果對應的 403；<paramref name="code"/> 給條件式檢查換成自己的機器代碼（例如事件旗標）。</summary>
    public static ServiceResult Denied(PermissionAdminCheck check, string? messageKey = null, string? code = null) => check switch
    {
        PermissionAdminCheck.McpChannel => ServiceResult.Forbidden(MessageRef.Of(MessageText.McpPermissionAdminDenied), "mcpPermissionAdminDenied"),
        _ => ServiceResult.Forbidden(MessageRef.Of(messageKey ?? MessageText.PermissionAdminRequired), code ?? "permissionAdminRequired"),
    };

    /// <summary>
    /// 先檢查，不是 <see cref="PermissionAdminCheck.Allowed"/> 就回對應的 403；是就在 Authorized 許可內執行 <paramref name="body"/>
    /// （body 要包含守衛寫入與它的存檔）。<paramref name="systemAdminVerified"/> 為 true 時 gate 自己再確認是啟用的系統管理員，
    /// 許可才帶 SystemAdminVerified（寫系統代碼列與成員表要它）；不是就回 403（沒有 code）。
    /// </summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> body, CancellationToken ct, bool systemAdminVerified = false)
    {
        if (typeof(T) != typeof(ServiceResult))
            throw new InvalidOperationException("PermissionAdminGate.RunAsync 的回傳型別必須是 ServiceResult（fail-closed）");
        if (channel.Channel == RequestChannel.Mcp) return (T)(object)Denied(PermissionAdminCheck.McpChannel);
        if (systemAdminVerified && await accounts.GetTrackedAsync(current.UserId, ct) is not { IsActive: true, IsSystemAdmin: true })
            return (T)(object)ServiceResult.Forbidden();
        var check = await CheckAsync(ct);
        if (check != PermissionAdminCheck.Allowed) return (T)(object)Denied(check);
        return await AuthorizationWriteScope.RunAsync(AuthorizationPermit.Authorized(systemAdminVerified), changes.HasPendingGuardChanges, body);
    }
}
