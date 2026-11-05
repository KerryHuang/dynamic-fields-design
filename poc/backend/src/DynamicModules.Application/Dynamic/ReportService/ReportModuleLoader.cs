using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ReportService;

/// <summary>一個模組與目前使用者對它的有效權限。</summary>
public sealed record ReportSide(Module Module, EffectivePermissions Perms);

/// <summary>定義涉及的模組：主、子、沿參照帶出的目標（key 是目標模組鍵）。不存在或未啟用的模組不在其中。</summary>
public sealed record ReportModuleSet(ReportSide? Main, ReportSide? Child, IReadOnlyDictionary<string, ReportSide> Targets);

/// <summary>載入報表涉及的模組並逐一授權（與 <see cref="ReportRunner"/> 同一套路徑：明細子模組走表頭授權）。</summary>
public class ReportModuleLoader(IModuleRepository modules, IDynamicAccessGuard guard)
{
    /// <summary>任何涉及的模組沒有 <paramref name="action"/> 權限就回 Denied。</summary>
    public async Task<(ReportModuleSet? Set, ServiceResult? Denied)> LoadAsync(ReportDefinition d, PermissionAction action, CancellationToken ct)
    {
        var main = await ActiveAsync(d.MainModuleKey, ct);
        ReportSide? mainSide = null;
        if (main is not null)
        {
            var access = await guard.AuthorizeAsync(main, action, ct);
            if (access.Permissions is null) return (null, access.Denied);
            mainSide = new ReportSide(main, access.Permissions);
        }

        Module? child = d.Child is null ? null : await ActiveAsync(d.Child.ModuleKey, ct);
        ReportSide? childSide = null;
        if (child is not null)
        {
            Module? parent = null;
            if (ModuleRelations.ParentKeyOf(child) is { } parentKey)
                parent = parentKey == main?.Key ? main : await ActiveAsync(parentKey, ct);
            if (parent is not null || ModuleRelations.ParentKeyOf(child) is null)
            {
                var access = parent is null
                    ? await guard.AuthorizeAsync(child, action, ct)
                    : await guard.AuthorizeDetailAsync(parent, child, action, ct);
                if (access.Permissions is null) return (null, access.Denied);
                childSide = new ReportSide(child, access.Permissions);
            }
        }

        var targets = new Dictionary<string, ReportSide>();
        foreach (var r in ReportRunner.AllRefs(d).Where(r => r.ViaFieldId is not null))
        {
            var source = r.Source == "main" ? main : r.Source == "child" ? child : null;
            var via = source?.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == r.ViaFieldId && f.Status == FieldStatus.Active);
            if (via?.Config.RefTarget is not { Kind: ReferenceTarget.ModuleKind } target || targets.ContainsKey(target.Key)) continue;
            if (await ActiveAsync(target.Key, ct) is not { } module) continue;
            var access = await guard.AuthorizeAsync(module, action, ct);
            if (access.Permissions is null) return (null, access.Denied);
            targets[target.Key] = new ReportSide(module, access.Permissions);
        }
        return (new ReportModuleSet(mainSide, childSide, targets), null);
    }

    private async Task<Module?> ActiveAsync(string key, CancellationToken ct) =>
        await modules.GetByKeyAsync(key, includeChildren: true, ct) is { Status: ModuleStatus.Active } m ? m : null;
}
