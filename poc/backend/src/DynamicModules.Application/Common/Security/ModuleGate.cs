using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Common.Security;

/// <summary>
/// 寫入類處理器的模組狀態關卡。Active 放行；Closed 回 409（資料唯讀）；其餘狀態維持 404。
/// Closed 的 409 只給有 Read 權限的人，沒權限的人得到 403，不洩漏「這個模組存在且已關閉」。
/// </summary>
public static class ModuleGate
{
    public const string ClosedMessage = "應用程式已關閉，資料唯讀";

    private const string NotFoundMessage = "模組不存在或未啟用";

    /// <summary>可以繼續回傳 null；否則是要直接回給呼叫端的結果。<paramref name="module"/> 須已載入 Blocks／Fields（授權要用）。</summary>
    public static async Task<ServiceResult?> RejectUnwritableAsync(Module module, IDynamicAccessGuard guard, CancellationToken ct)
    {
        if (module.IsWritable()) return null;
        if (module.Status != ModuleStatus.Closed) return ServiceResult.NotFound(NotFoundMessage);
        var access = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        return access.Permissions is null ? access.Denied : ServiceResult.Conflict(ClosedMessage);
    }
}
