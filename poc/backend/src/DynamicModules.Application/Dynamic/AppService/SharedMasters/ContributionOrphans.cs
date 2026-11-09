using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.AppService.SharedMasters;

/// <summary>
/// 同一個應用程式重新開啟時接回它留下的孤兒（共用主檔設計 §7 刪除第 6 點）：同一個前擁有者＋同一種類＋同一個代號就接回，
/// 欄位型別不同時不接回、新欄位沿用原代號，並刪掉舊孤兒列與它的選項孤兒列（舊欄位只留成停用欄位，失去接回資格）。
/// </summary>
public sealed class ContributionOrphans(ISharedMasterRepository shared, IAppRepository apps, IModuleRepository modules)
{
    public sealed record Result(List<ContributionInput> Inputs, List<AppOrphan> Consumed, List<AppOrphan> Stale, List<string> Warnings);

    public async Task<Result> MatchAsync(AppPackageData package, CancellationToken ct)
    {
        var orphans = await shared.ListOrphansAsync(package.Manifest.Id, ct);
        var inputs = new List<ContributionInput>();
        var consumed = new List<AppOrphan>();
        var stale = new List<AppOrphan>();
        var warnings = new List<string>();
        foreach (var c in package.Contributions)
        {
            var prefix = $"{c.Master}/{c.Module}";
            var module = await ModuleAsync(c.Master, c.Module, ct);
            if (module is null) continue;
            var fields = module.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);
            var reattachFields = new Dictionary<string, long>();
            var reattachOptions = new Dictionary<string, long>();
            foreach (var f in c.Fields)
            {
                var orphan = orphans.FirstOrDefault(o => o.Kind == OrphanKind.Field && o.Name == $"{prefix}.{f.Code}");
                if (orphan?.FieldId is not { } id || !fields.TryGetValue(id, out var existing)) continue;
                if (existing.Type != f.Type)
                {
                    // 舊欄位的選項孤兒一併失去接回資格（舊選項本身保留）：新欄位可能合法重用同一個選項代號，
                    // 之後再刪一次時同名的選項孤兒才不會撞 (FormerAppKey, Kind, Name)（審查必修 7）
                    stale.Add(orphan);
                    stale.AddRange(orphans.Where(o => o.Kind == OrphanKind.Option && o.OptionId is { } oid && existing.Options.Any(x => x.Id == oid)));
                    warnings.Add($"「{existing.LabelI18n.Resolve(I18nText.FallbackLocale)}」的型別和套件不同，沒有接回；舊欄位保留為停用，新欄位沿用代號 {f.Code}");
                    continue;
                }
                consumed.Add(orphan);
                reattachFields[f.Code] = id;
                foreach (var option in f.Options)
                    if (orphans.FirstOrDefault(o => o.Kind == OrphanKind.Option && o.Name == $"{prefix}.{option.Code}") is { OptionId: { } optionId } oo
                        && existing.Options.Any(x => x.Id == optionId))
                    {
                        consumed.Add(oo);
                        reattachOptions[option.Code] = optionId;
                    }
            }
            // 套件裡的選項代號若撞上沒被接回的選項孤兒（例如原欄位整個被拿掉、新欄位沿用代號），一樣解除接回資格，
            // 否則下次刪除時同名的選項孤兒會撞 (FormerAppKey, Kind, Name)（審查待決 6）。選項本身保留
            foreach (var option in c.Fields.SelectMany(f => f.Options))
                foreach (var o in orphans.Where(o => o.Kind == OrphanKind.Option && o.Name == $"{prefix}.{option.Code}"))
                    if (!consumed.Contains(o) && !stale.Contains(o)) stale.Add(o);
            long? reattachBlock = null;
            if (orphans.FirstOrDefault(o => o.Kind == OrphanKind.Block && o.Name == $"{prefix}.{c.Block.Code}") is { BlockId: { } blockId } ob
                && module.Blocks.Any(b => b.Id == blockId))
            {
                consumed.Add(ob);
                reattachBlock = blockId;
            }
            inputs.Add(new ContributionInput(prefix, module.Key, c.Block.Code, c.Block.Name, c.Fields, reattachBlock, reattachFields, reattachOptions));
        }
        return new Result(inputs, consumed, stale, warnings);
    }

    private async Task<Module?> ModuleAsync(string masterKey, string code, CancellationToken ct) =>
        await apps.GetByKeyAsync(masterKey, ct) is { } master && master.Modules.FirstOrDefault(m => m.Code == code) is { } member
            ? await modules.GetByIdAsync(member.ModuleId, includeChildren: true, ct)
            : null;
}
