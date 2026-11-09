using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Common.Security;

public class DynamicAccessGuard(
    ICurrentUser user, IProgramRepository programs, IPermissionRepository permissions, IModuleRepository modules, ISystemIdentity system)
    : IDynamicAccessGuard
{
    public const string DesignerProgramCode = "DYN00000";

    /// <summary>事件效果的系統身分：所有動作、不限資料範圍、沒有隱藏或唯讀欄位（設計 §4.2）。</summary>
    private static readonly EffectivePermissions SystemPermissions = new(
        Enum.GetValues<PermissionAction>().ToHashSet(), DataScope.All, null,
        new HashSet<long>(), new HashSet<long>(), new HashSet<long>(), "system");

    public async Task<AccessResult> AuthorizeAsync(Module module, PermissionAction action, CancellationToken ct)
    {
        if (ClosedBlocks(module, action)) return Deny();
        if (system.Active) return new AccessResult(SystemPermissions, null);
        var program = await programs.GetAsync(module.ProgramCode, ct);
        if (program is not { Active: true }) return Deny();

        var inputs = await permissions.LoadInputsAsync(user.UserId, module, ct);
        if (inputs is null) return Deny();

        var effective = PermissionResolver.Resolve(module, inputs, null, await HiddenSourceFieldsAsync(module, inputs, ct), action);
        return effective.Can(action) ? new AccessResult(effective, null) : Deny();
    }

    public async Task<AccessResult> AuthorizeDetailAsync(Module parent, Module child, PermissionAction action, CancellationToken ct)
    {
        if (ClosedBlocks(parent, action) || ClosedBlocks(child, action)) return Deny();
        if (system.Active) return new AccessResult(SystemPermissions, null);
        var program = await programs.GetAsync(parent.ProgramCode, ct);
        if (program is not { Active: true }) return Deny();

        var parentInputs = await permissions.LoadInputsAsync(user.UserId, parent, ct);
        var childInputs = await permissions.LoadInputsAsync(user.UserId, child, ct);
        if (parentInputs is null || childInputs is null) return Deny();

        // 動作軸與資料範圍來自表頭的授權列；欄位例外與選項範圍來自明細模組
        var effective = PermissionResolver.Resolve(child, DetailInputs(parentInputs, childInputs), parent, forAction: action);
        return effective.Can(action) ? new AccessResult(effective, null) : Deny();
    }

    /// <summary>
    /// 已關閉的模組只剩讀取與匯出。必須在 Resolve 之前、不看使用者身分判斷：PermissionResolver 對系統管理員回傳所有動作，
    /// 放在結果之後檢查就擋不到管理員。這是各 handler 檢查之外的第二道防線。
    /// </summary>
    private static bool ClosedBlocks(Module module, PermissionAction action) =>
        module.Status == ModuleStatus.Closed && action is not (PermissionAction.Read or PermissionAction.PrintExport);

    private static PermissionInputs DetailInputs(PermissionInputs parentInputs, PermissionInputs childInputs) =>
        parentInputs with { FieldPermissions = childInputs.FieldPermissions, OptionScopes = childInputs.OptionScopes, FieldGrants = childInputs.FieldGrants };

    /// <summary>module 有以明細欄位為加總對象的彙總時，算出使用者在各明細模組看不到的欄位（沿用明細模組自己的隱藏規則）。</summary>
    private async Task<IReadOnlySet<long>?> HiddenSourceFieldsAsync(Module module, PermissionInputs inputs, CancellationToken ct)
    {
        if (!module.Blocks.SelectMany(b => b.Fields).Any(f => f is { Type: FieldType.Rollup, Config.Rollup.AggregateFieldId: not null }))
            return null;
        var hidden = new HashSet<long>();
        foreach (var (child, _) in await modules.ListDetailModulesAsync(module.Key, ct))
        {
            var childInputs = await permissions.LoadInputsAsync(user.UserId, child, ct);
            if (childInputs is null) continue;
            hidden.UnionWith(PermissionResolver.Resolve(child, DetailInputs(inputs, childInputs), module).HiddenFieldIds);
        }
        return hidden;
    }

    public async Task<IReadOnlySet<PermissionAction>> DesignerActionsAsync(CancellationToken ct)
    {
        var inputs = await permissions.LoadProgramInputsAsync(user.UserId, DesignerProgramCode, ct);
        return inputs is null ? new HashSet<PermissionAction>() : PermissionResolver.ResolveProgram(DesignerProgramCode, inputs);
    }

    private static AccessResult Deny() => new(null, ServiceResult.Forbidden());
}
