using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Common.Security;

public class DynamicAccessGuard(
    ICurrentUser user, IProgramRepository programs, IPermissionRepository permissions, IModuleRepository modules)
    : IDynamicAccessGuard
{
    public const string DesignerProgramCode = "DYN00000";

    public async Task<AccessResult> AuthorizeAsync(Module module, PermissionAction action, CancellationToken ct)
    {
        var program = await programs.GetAsync(module.ProgramCode, ct);
        if (program is not { Active: true }) return Deny();

        var inputs = await permissions.LoadInputsAsync(user.UserId, module, ct);
        if (inputs is null) return Deny();

        var effective = PermissionResolver.Resolve(module, inputs, null, await HiddenSourceFieldsAsync(module, inputs, ct));
        return effective.Can(action) ? new AccessResult(effective, null) : Deny();
    }

    public async Task<AccessResult> AuthorizeDetailAsync(Module parent, Module child, PermissionAction action, CancellationToken ct)
    {
        var program = await programs.GetAsync(parent.ProgramCode, ct);
        if (program is not { Active: true }) return Deny();

        var parentInputs = await permissions.LoadInputsAsync(user.UserId, parent, ct);
        var childInputs = await permissions.LoadInputsAsync(user.UserId, child, ct);
        if (parentInputs is null || childInputs is null) return Deny();

        // 動作軸與資料範圍來自表頭的授權列；欄位例外與選項範圍來自明細模組
        var effective = PermissionResolver.Resolve(child, DetailInputs(parentInputs, childInputs), parent);
        return effective.Can(action) ? new AccessResult(effective, null) : Deny();
    }

    private static PermissionInputs DetailInputs(PermissionInputs parentInputs, PermissionInputs childInputs) =>
        parentInputs with { FieldPermissions = childInputs.FieldPermissions, OptionScopes = childInputs.OptionScopes };

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
