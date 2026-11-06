using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Formulas;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Rules;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.DeleteService;

/// <summary>刪除計畫：檢查結果，加上實際要刪的目標 Id（執行時重新載入追蹤實體）。</summary>
public record DeletePlan(DeleteCheckResponse Check, long ModuleId, IReadOnlyList<long> FieldIds, IReadOnlyList<long> BlockIds);

/// <summary>
/// 盤點模組、欄位、區塊能不能刪（未啟用、無資料、未被引用，規則見設計文件）並算出清除筆數。
/// 只讀取，不修改；命令與預檢共用，確保預告與實際行為一致。
/// </summary>
public class DeletePlanner(IDeletionRepository data)
{
    public async Task<DeletePlan?> PlanAsync(DeleteTarget target, string? key, long id, CancellationToken ct)
    {
        var all = await data.ListModulesAsync(ct);
        var module = target switch
        {
            DeleteTarget.Module => all.FirstOrDefault(m => m.Key == key),
            DeleteTarget.Field => all.FirstOrDefault(m => m.Blocks.Any(b => b.Fields.Any(f => f.Id == id))),
            _ => all.FirstOrDefault(m => m.Blocks.Any(b => b.Id == id))
        };
        if (module is null) return null;

        var blockers = new List<DeleteBlocker>();
        var views = await data.ListViewsAsync(ct);
        var reports = await data.ListReportsAsync(ct);
        IReadOnlyList<Field> fields;
        IReadOnlyList<Block> blocks;
        switch (target)
        {
            case DeleteTarget.Module:
                fields = module.Blocks.SelectMany(b => b.Fields).ToList();
                blocks = module.Blocks;
                await ModuleBlockersAsync(module, fields, all, reports, blockers, ct);
                break;
            case DeleteTarget.Field:
                fields = module.Blocks.SelectMany(b => b.Fields).Where(f => f.Id == id).ToList();
                blocks = [];
                await FieldBlockersAsync(module, fields, [], all, views, reports, blockers, ct);
                break;
            default:
                var block = module.Blocks.Single(b => b.Id == id);
                fields = block.Fields;
                blocks = [block];
                if (module.Blocks.Count == 1)
                    blockers.Add(new(DeleteBlockerKind.LastBlock, module.Key, block.Id, Name(block.LabelI18n, $"#{block.Id}")));
                await FieldBlockersAsync(module, fields, [block.Id], all, views, reports, blockers, ct);
                break;
        }

        var fieldIds = fields.Select(f => f.Id).ToList();
        var purge = target == DeleteTarget.Module
            ? new PurgeCounts(await data.CountRecordsAsync(module.Id, deleted: true, ct),
                await data.CountIndexRowsByModuleAsync(module.Id, ct), await data.CountHistoryRowsByModuleAsync(module.Id, ct))
            : await FieldPurgeAsync(module, fields, ct);
        var distinct = blockers.DistinctBy(b => (b.Kind, b.ModuleKey, b.Id)).ToList();
        return new DeletePlan(new DeleteCheckResponse(distinct.Count == 0, distinct, purge), module.Id, fieldIds,
            blocks.Select(b => b.Id).ToList());
    }

    private async Task<PurgeCounts> FieldPurgeAsync(Module module, IReadOnlyList<Field> fields, CancellationToken ct)
    {
        var keys = fields.Select(f => f.Key).ToList();
        var ids = fields.Select(f => f.Id).ToList();
        // 軟刪除記錄中帶有這些欄位值的筆數（清除時從 ValuesJson 拿掉）
        var soft = (await data.ListRecordsWithKeysAsync(module.Id, keys, ct)).Count(r => r.DeleteFlag);
        return new PurgeCounts(soft, await data.CountIndexRowsByFieldsAsync(ids, ct), await data.CountHistoryRowsByFieldsAsync(ids, ct));
    }

    private async Task ModuleBlockersAsync(
        Module module, IReadOnlyList<Field> fields, List<Module> all, List<Report> reports, List<DeleteBlocker> blockers, CancellationToken ct)
    {
        if (module.Status == ModuleStatus.Active)
            blockers.Add(new(DeleteBlockerKind.ModuleActive, module.Key, module.Id, Name(module.NameI18n, module.Key)));
        var live = await data.CountRecordsAsync(module.Id, deleted: false, ct);
        if (live > 0)
            blockers.Add(new(DeleteBlockerKind.LiveRecords, module.Key, module.Id, Name(module.NameI18n, module.Key), live));

        var ownIds = fields.Select(f => f.Id).ToHashSet();
        foreach (var other in all.Where(m => m.Id != module.Id))
            foreach (var f in other.Blocks.SelectMany(b => b.Fields))
            {
                if (f is { Type: FieldType.Reference, Config.RefTarget: { Kind: ReferenceTarget.ModuleKind } t } && t.Key == module.Key)
                    blockers.Add(FieldBlocker(DeleteBlockerKind.ReferenceField, other, f));
                AddFieldSourceBlockers(other, f, ownIds, blockers);
            }

        AddReportBlockers(reports, ownIds, [module.Key], blockers);
    }

    /// <summary>刪欄位或區塊：targetFields 是要刪的欄位，blockIds 是要刪的區塊（欄位刪除時為空）。</summary>
    private async Task FieldBlockersAsync(
        Module module, IReadOnlyList<Field> targetFields, IReadOnlyCollection<long> blockIds, List<Module> all,
        List<ListView> views, List<Report> reports, List<DeleteBlocker> blockers, CancellationToken ct)
    {
        var ids = targetFields.Select(f => f.Id).ToHashSet();
        foreach (var f in targetFields)
        {
            if (f.Status == FieldStatus.Active)
                blockers.Add(FieldBlocker(DeleteBlockerKind.FieldActive, module, f));
            if (module.TitleFieldId == f.Id)
                blockers.Add(FieldBlocker(DeleteBlockerKind.TitleField, module, f));
        }

        // 衍生欄位（公式、彙總）的值由其他欄位算出，不算資料；其餘看未軟刪除記錄是否有非空值
        var dataFields = targetFields.Where(f => f.Type is not (FieldType.Formula or FieldType.Rollup)).ToList();
        var counts = await data.CountLiveRecordsWithValueAsync(module.Id, dataFields.Select(f => f.Key).ToList(), ct);
        foreach (var f in dataFields.Where(f => counts.ContainsKey(f.Key)))
            blockers.Add(FieldBlocker(DeleteBlockerKind.FieldData, module, f) with { Count = counts[f.Key] });

        // 引用來源：停用的公式、規則、欄位也算（重新啟用時會壞掉）；一起被刪的欄位互相引用不算
        foreach (var m in all)
            foreach (var f in m.Blocks.SelectMany(b => b.Fields).Where(f => !ids.Contains(f.Id)))
                AddFieldSourceBlockers(m, f, ids, blockers);
        foreach (var m in all)
            foreach (var r in m.Rules)
            {
                var hitsField = RuleReferences.FieldIdsOf(r).Overlaps(ids);
                var hitsBlock = r.Targets.Any(t => RuleTarget.TryParse(t, out var rt) && rt is BlockTarget b && blockIds.Contains(b.BlockId));
                if (hitsField || hitsBlock)
                    blockers.Add(new(DeleteBlockerKind.Rule, m.Key, r.Id, Name(r.MessageI18n, $"{r.Kind} #{r.Id}")));
            }
        foreach (var v in views)
        {
            var refs = v.Columns.Concat(v.Sort.Select(s => s.Field)).ToHashSet();
            if (v.Filter is { } filter) refs.UnionWith(RuleReferences.ConditionFieldIdsOf(filter));
            if (refs.Overlaps(ids))
                blockers.Add(new(DeleteBlockerKind.View, all.FirstOrDefault(m => m.Id == v.ModuleId)?.Key, v.Id, Name(v.NameI18n, $"#{v.Id}")));
        }
        AddReportBlockers(reports, ids, [], blockers);
    }

    /// <summary>f 是否是公式、彙總或預設帶值設定，引用了 targetIds 裡的欄位。</summary>
    private static void AddFieldSourceBlockers(Module owner, Field f, IReadOnlySet<long> targetIds, List<DeleteBlocker> blockers)
    {
        if (f.Type == FieldType.Formula && FormulaGraph.RefsOf(f).Overlaps(targetIds))
            blockers.Add(FieldBlocker(DeleteBlockerKind.Formula, owner, f));
        if (f.Type == FieldType.Rollup && ModuleRelations.RollupRefsOf(f).Overlaps(targetIds))
            blockers.Add(FieldBlocker(DeleteBlockerKind.Rollup, owner, f));
        if (f.Config.ParentDefaultFieldId is { } p && targetIds.Contains(p))
            blockers.Add(FieldBlocker(DeleteBlockerKind.ParentDefault, owner, f));
    }

    private static void AddReportBlockers(List<Report> reports, IReadOnlySet<long> fieldIds, IReadOnlyCollection<string> moduleKeys, List<DeleteBlocker> blockers)
    {
        foreach (var r in reports)
        {
            var refs = ReportReferences.TryOf(r.DefinitionJson);
            // 定義讀不出來時無法證明沒引用，視為引用（fail-closed）
            if (refs is null || refs.FieldIds.Overlaps(fieldIds) || moduleKeys.Any(refs.ModuleKeys.Contains))
                blockers.Add(new(DeleteBlockerKind.Report, null, r.Id, r.Name));
        }
    }

    private static DeleteBlocker FieldBlocker(string kind, Module m, Field f) =>
        new(kind, m.Key, f.Id, Name(f.LabelI18n, f.Key));

    private static string Name(I18nText? text, string fallback)
    {
        var resolved = text?.Resolve(I18nText.FallbackLocale);
        return string.IsNullOrWhiteSpace(resolved) ? fallback : resolved;
    }
}
