using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 使用者看不到的欄位，含明細來源隱藏（同 DynamicAccessGuard：彙總明細隱藏欄位的彙總、以及引用它的公式一起隱藏）。
/// 給拿不到 guard 結果的路徑用（記錄預覽、歷程遮蔽的備案、臨時讀取權）；讀不到權限輸入一律回「不成立」，由呼叫端當成全部隱藏。
/// </summary>
public sealed class ApprovalHiddenFields(IPermissionRepository permissions, IModuleRepository modules)
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

    /// <summary>
    /// 明細來源隱藏：module 有以明細欄位為加總對象的彙總時，使用者在各明細模組看不到的欄位（動作軸取 inputs，欄位例外取明細）。
    /// 沒有這類彙總回 (true, null)；任一明細讀不到權限輸入回 (false, null)（不放行）。
    /// </summary>
    public async Task<(bool Ok, IReadOnlySet<long>? Source)> SourceAsync(Module module, PermissionInputs inputs, string userId, CancellationToken ct)
    {
        if (!module.Blocks.SelectMany(b => b.Fields).Any(f => f is { Type: FieldType.Rollup, Config.Rollup.AggregateFieldId: not null }))
            return (true, null);
        var hidden = new HashSet<long>();
        foreach (var (child, _) in await modules.ListDetailModulesAsync(module.Key, ct))
        {
            var childInputs = await permissions.LoadInputsAsync(userId, child, ct);
            if (childInputs is null) return (false, null);
            hidden.UnionWith(PermissionResolver.Resolve(child, DetailInputs(inputs, childInputs), module).HiddenFieldIds);
        }
        return (true, hidden);
    }

    public static PermissionInputs DetailInputs(PermissionInputs parentInputs, PermissionInputs childInputs) =>
        parentInputs with { FieldPermissions = childInputs.FieldPermissions, OptionScopes = childInputs.OptionScopes, FieldGrants = childInputs.FieldGrants };
}
