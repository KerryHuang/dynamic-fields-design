using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Application.Dynamic.EventService;
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
    /// <param name="alsoDeleting">
    /// 同一個動作一起刪除的模組鍵（刪除整個應用程式時是全部成員）。集合內的模組彼此的引用不算被引用，
    /// 模組本身的狀態也不另擋（應用程式已先確認是關閉的）；其餘規則不變。只對 <see cref="DeleteTarget.Module"/> 有意義。
    /// </param>
    public async Task<DeletePlan?> PlanAsync(
        DeleteTarget target, string? key, long id, CancellationToken ct, IReadOnlySet<string>? alsoDeleting = null)
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
        var dashboards = await data.ListDashboardLayoutsAsync(ct);
        var conversions = await data.ListConversionsAsync(ct);
        IReadOnlyList<Field> fields;
        IReadOnlyList<Block> blocks;
        switch (target)
        {
            case DeleteTarget.Module:
                fields = module.Blocks.SelectMany(b => b.Fields).ToList();
                blocks = module.Blocks;
                await ModuleBlockersAsync(module, fields, all, reports, dashboards, conversions, blockers, alsoDeleting, ct);
                break;
            case DeleteTarget.Field:
                fields = module.Blocks.SelectMany(b => b.Fields).Where(f => f.Id == id).ToList();
                blocks = [];
                await FieldBlockersAsync(module, fields, [], all, views, reports, dashboards, conversions, blockers, ct);
                break;
            default:
                var block = module.Blocks.Single(b => b.Id == id);
                fields = block.Fields;
                blocks = [block];
                if (module.Blocks.Count == 1)
                    blockers.Add(new(DeleteBlockerKind.LastBlock, module.Key, block.Id, Name(block.LabelI18n, $"#{block.Id}")));
                await FieldBlockersAsync(module, fields, [block.Id], all, views, reports, dashboards, conversions, blockers, ct);
                break;
        }

        await AddEffectBlockersAsync(target, module, fields, all, alsoDeleting, blockers, ct);

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
        Module module, IReadOnlyList<Field> fields, List<Module> all, List<Report> reports, List<DashboardLayoutSource> dashboards,
        List<Conversion> conversions, List<DeleteBlocker> blockers, IReadOnlySet<string>? alsoDeleting, CancellationToken ct)
    {
        if (alsoDeleting is null && module.Status is ModuleStatus.Active or ModuleStatus.Closed)
            blockers.Add(new(DeleteBlockerKind.ModuleActive, module.Key, module.Id, Name(module.NameI18n, module.Key)));
        var live = await data.CountRecordsAsync(module.Id, deleted: false, ct);
        if (live > 0)
            blockers.Add(new(DeleteBlockerKind.LiveRecords, module.Key, module.Id, Name(module.NameI18n, module.Key), live));

        var ownIds = fields.Select(f => f.Id).ToHashSet();
        foreach (var other in all.Where(m => m.Id != module.Id && alsoDeleting?.Contains(m.Key) != true))
            foreach (var f in other.Blocks.SelectMany(b => b.Fields))
            {
                if (f is { Type: FieldType.Reference, Config.RefTarget: { Kind: ReferenceTarget.ModuleKind } t } && t.Key == module.Key)
                    blockers.Add(FieldBlocker(DeleteBlockerKind.ReferenceField, other, f));
                AddFieldSourceBlockers(other, f, ownIds, blockers);
            }

        AddReportBlockers(reports, ownIds, [module.Key], blockers);
        AddDashboardBlockers(dashboards, ownIds, blockers);
        // 轉換定義的來源、目標與明細模組全在這次一起刪除的集合內：定義跟著應用程式一起移除（ModuleRemover），不算引用
        var outside = alsoDeleting is null ? conversions : conversions.Where(c => !InsideSet(c, all, alsoDeleting)).ToList();
        AddConversionBlockers(outside, all, ownIds, [module.Id], [module.Key], blockers);
    }

    /// <summary>事件、跨記錄檢查、分錄設定引用了要刪的模組或欄位。同一個動作一起刪除的集合內的引用不算（ModuleRemover 會一起移除）。</summary>
    private async Task AddEffectBlockersAsync(
        DeleteTarget target, Module module, IReadOnlyList<Field> fields, List<Module> all,
        IReadOnlySet<string>? alsoDeleting, List<DeleteBlocker> blockers, CancellationToken ct)
    {
        var ids = fields.Select(f => f.Id).ToHashSet();
        var isModule = target == DeleteTarget.Module;
        bool Inside(IEnumerable<string> keys) => alsoDeleting is not null && keys.All(alsoDeleting.Contains);
        string? KeyOf(long moduleId) => all.FirstOrDefault(m => m.Id == moduleId)?.Key;

        foreach (var e in await data.ListEventsAsync(ct))
        {
            var refs = EffectReferences.Of(e, KeyOf(e.SourceModuleId) ?? "");
            if ((isModule && refs.ModuleKeys.Contains(module.Key) || refs.FieldIds.Overlaps(ids)) && !(isModule && Inside(refs.ModuleKeys)))
                blockers.Add(new(DeleteBlockerKind.Event, KeyOf(e.SourceModuleId), e.Id, Name(e.NameI18n, e.Key)));
        }
        foreach (var c in await data.ListChecksAsync(ct))
        {
            var key = KeyOf(c.ModuleId);
            if ((isModule && c.ModuleId == module.Id || EffectReferences.Of(c).Overlaps(ids)) && !(isModule && key is not null && Inside([key])))
                blockers.Add(new(DeleteBlockerKind.Check, key, c.Id, Name(c.NameI18n, c.Key)));
        }
        foreach (var l in await data.ListLedgersAsync(ct))
        {
            var key = KeyOf(l.ModuleId);
            if ((isModule && l.ModuleId == module.Id || EffectReferences.Of(l).Overlaps(ids)) && !(isModule && key is not null && Inside([key])))
                blockers.Add(new(DeleteBlockerKind.Ledger, key, l.Id, key ?? ""));
        }
    }

    private static bool InsideSet(Conversion c, List<Module> all, IReadOnlySet<string> keys)
    {
        var d = c.Definition;
        var moduleKeys = new List<string?>
        {
            all.FirstOrDefault(m => m.Id == c.SourceModuleId)?.Key, all.FirstOrDefault(m => m.Id == c.TargetModuleId)?.Key,
            d.Lines?.SourceModuleKey, d.Lines?.TargetModuleKey
        };
        return moduleKeys.Where(k => k is not null).All(k => keys.Contains(k!));
    }

    /// <summary>刪欄位或區塊：targetFields 是要刪的欄位，blockIds 是要刪的區塊（欄位刪除時為空）。</summary>
    private async Task FieldBlockersAsync(
        Module module, IReadOnlyList<Field> targetFields, IReadOnlyCollection<long> blockIds, List<Module> all,
        List<ListView> views, List<Report> reports, List<DashboardLayoutSource> dashboards, List<Conversion> conversions,
        List<DeleteBlocker> blockers, CancellationToken ct)
    {
        var ids = targetFields.Select(f => f.Id).ToHashSet();
        foreach (var f in targetFields)
        {
            if (f.Status == FieldStatus.Active)
                blockers.Add(FieldBlocker(DeleteBlockerKind.FieldActive, module, f));
            if (module.TitleFieldIds().Contains(f.Id))
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
        AddDashboardBlockers(dashboards, ids, blockers);
        AddConversionBlockers(conversions, all, ids, [], [], blockers);
        foreach (var m in all.Where(m => m.Workflow is { } w && ids.Contains(w.StatusFieldId)))
            blockers.Add(new(DeleteBlockerKind.Workflow, m.Key, m.Workflow!.Id, Name(m.NameI18n, m.Key)));
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
        if (f.Config.Fill is { } fill && fill.Map.Any(m => targetIds.Contains(m.From) || targetIds.Contains(m.To)))
            blockers.Add(FieldBlocker(DeleteBlockerKind.Fill, owner, f));
        // 目標狀態守衛引用的狀態欄位：沿用「帶值」這個阻擋類型（都是參照欄位的設定引用了別的欄位）
        else if (f.Config.RefGuard is { } guard && targetIds.Contains(guard.FieldId))
            blockers.Add(FieldBlocker(DeleteBlockerKind.Fill, owner, f));
    }

    /// <summary>轉換定義引用了這些欄位（對應、追溯、數量欄位），或引用了這些模組（來源、目標、明細模組）。</summary>
    private static void AddConversionBlockers(
        List<Conversion> conversions, List<Module> all, IReadOnlySet<long> fieldIds, IReadOnlyCollection<long> moduleIds,
        IReadOnlyCollection<string> moduleKeys, List<DeleteBlocker> blockers)
    {
        foreach (var c in conversions)
        {
            var d = c.Definition;
            var refs = d.Header.Copy.SelectMany(m => new[] { m.From, m.To }).Append(d.Header.SourceFieldId).ToList();
            if (d.Lines is { } l)
                refs.AddRange(l.Copy.SelectMany(m => new[] { m.From, m.To }).Concat([l.Quantity.From, l.Quantity.To, l.SourceLineFieldId]));
            var hitsModule = moduleIds.Contains(c.SourceModuleId) || moduleIds.Contains(c.TargetModuleId)
                             || (d.Lines is { } lines && (moduleKeys.Contains(lines.SourceModuleKey) || moduleKeys.Contains(lines.TargetModuleKey)));
            // 刪模組時，被刪模組自己的欄位出現在定義裡也算引用（定義是屬於這個模組關係的）
            if (hitsModule || refs.Any(fieldIds.Contains))
                blockers.Add(new(DeleteBlockerKind.Conversion, all.FirstOrDefault(m => m.Id == c.SourceModuleId)?.Key, c.Id, Name(c.NameI18n, c.Key)));
        }
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

    /// <summary>儀表板卡片的排序鍵可指向欄位；指派版、自己的儀表板、個人版都算。版面讀不出來時視為引用（fail-closed）。</summary>
    private static void AddDashboardBlockers(List<DashboardLayoutSource> dashboards, IReadOnlySet<long> fieldIds, List<DeleteBlocker> blockers)
    {
        foreach (var d in dashboards)
        {
            var refs = DashboardReferences.TryFieldIdsOf(d.LayoutJson);
            if (refs is null || refs.Overlaps(fieldIds))
                blockers.Add(new(d.IsPersonal ? DeleteBlockerKind.PersonalDashboard : DeleteBlockerKind.Dashboard, null, d.Id, d.Name));
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
