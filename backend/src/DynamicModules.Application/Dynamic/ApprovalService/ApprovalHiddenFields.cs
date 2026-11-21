using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 使用者看不到的欄位，含明細來源隱藏（同 DynamicAccessGuard：彙總明細隱藏欄位的彙總、以及引用它的公式一起隱藏）。
/// 給拿不到 guard 結果的路徑用（記錄預覽、歷程遮蔽的備案、臨時讀取權）；讀不到權限輸入一律回「不成立」，由呼叫端當成全部隱藏。
/// </summary>
public sealed class ApprovalHiddenFields(IPermissionRepository permissions, RollupSourceMasks sources)
{
    /// <summary>使用者自己的群組欄位例外＋明細來源隱藏；讀不到權限輸入回 null（全部隱藏）。</summary>
    public async Task<IReadOnlySet<long>?> ForUserAsync(Module module, string userId, CancellationToken ct)
    {
        var inputs = await permissions.LoadInputsAsync(userId, module, ct);
        if (inputs is null) return null;
        var (ok, source) = await SourceAsync(module, inputs, userId, ct);
        return ok ? PermissionResolver.Resolve(module, inputs, null, source).HiddenFieldIds : null;
    }

    /// <summary>使用者自己的群組欄位例外下不能寫的欄位（隱藏＋唯讀）；讀不到權限輸入回 null（全部不能寫）。</summary>
    public async Task<IReadOnlySet<long>?> UnwritableForUserAsync(Module module, string userId, CancellationToken ct)
    {
        var inputs = await permissions.LoadInputsAsync(userId, module, ct);
        if (inputs is null) return null;
        var (ok, source) = await SourceAsync(module, inputs, userId, ct);
        if (!ok) return null;
        var p = PermissionResolver.Resolve(module, inputs, null, source);
        return p.HiddenFieldIds.Union(p.ReadOnlyFieldIds).ToHashSet();
    }

    /// <summary>彙總來源的遮罩（<see cref="RollupSourceMasks"/>）；任一來源讀不到權限輸入回 (false, null)（不放行）。</summary>
    public Task<(bool Ok, RollupSourceMask? Source)> SourceAsync(Module module, PermissionInputs inputs, string userId, CancellationToken ct) =>
        sources.LoadAsync(module, inputs, userId, ct);

    public static PermissionInputs DetailInputs(PermissionInputs parentInputs, PermissionInputs childInputs) =>
        parentInputs with { FieldPermissions = childInputs.FieldPermissions, OptionScopes = childInputs.OptionScopes, FieldGrants = childInputs.FieldGrants };
}
