using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using static DynamicModules.Application.Dynamic.TemplateService.TemplateGraph;

namespace DynamicModules.Application.Dynamic.AppService;

// 格式 4 的引用層：events、checks、ledgers 的代號都要在套件內找得到，並且做三項只看套件就能判斷的檢查：
// 自訂效果不收、複製被隱藏的來源欄位需要 allowHiddenSource、套件內的事件圖不得成環。
// 欄位型別、與資料庫既有事件合併後的循環，開啟時由既有的設計器指令負責，失敗整批回滾。
public static partial class AppPackageValidator
{
    public const int MaxEvents = 100, MaxChecks = 100;

    private static void V4References(AppPackageData p, Dictionary<string, TemplateField> fields, MasterScope scope, List<string> errors)
    {
        var modules = p.Modules.ToDictionary(m => m.Code, m => m);
        var optionNames = p.Modules.SelectMany(m => m.Blocks.SelectMany(b => b.Fields).SelectMany(f => f.Options.Select(o => $"{m.Code}.{o.Code}")))
            .ToHashSet();
        var masterErrors = new List<string>();
        var lookups = MasterLookups(scope, masterErrors,
            code => modules.ContainsKey(code) ? code : null,
            name => fields.ContainsKey(name) ? 1 : null,
            name => optionNames.Contains(name) ? 1 : null,
            AppReportCodec.TypesOf(p.Modules));
        var workflowOf = p.Workflows.Where(w => w is not null && w.Module is not null).GroupBy(w => w.Module).ToDictionary(g => g.Key, g => g.First());
        var hidden = p.FieldAccess.Where(a => a is { Access: FieldAccess.Hidden }).Select(a => a.Field).ToHashSet();
        // 貢獻欄位是限定可見的欄位，加上引用它們的公式（逐層），和執行期 EventValidator 的規則一致（審查待決 7）
        var restricted = p.Contributions.Where(c => c?.Master is not null && c.Module is not null)
            .SelectMany(c => c.Fields.Where(f => f is not null).Select(f => Qualify($"{c.Master}/{c.Module}", f.Code))).ToHashSet();
        var formulas = p.Modules.SelectMany(m => m.Blocks.SelectMany(b => b.Fields).Select(f => (Scope: m.Code, Field: f)))
            .Concat(p.Contributions.Where(c => c?.Master is not null && c.Module is not null).SelectMany(c => c.Fields.Where(f => f is not null).Select(f => (Scope: $"{c.Master}/{c.Module}", Field: f))))
            .Select(x => (Name: Qualify(x.Scope, x.Field.Code), Refs: FormulaOf(x.Field) is { } fx ? FormulaCodes(fx).Select(t => t.Contains('.') ? t : Qualify(x.Scope, t)).ToList() : []))
            .Where(x => x.Refs.Count > 0).ToList();
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var f in formulas.Where(f => !restricted.Contains(f.Name) && f.Refs.Any(restricted.Contains)))
                grew |= restricted.Add(f.Name);
        }
        hidden.UnionWith(restricted);
        // 貢獻內 fieldAccess 標成 hidden 的欄位（第三輪 5）；貢獻欄位本來就在 restricted 裡，這裡讓兩條規則明確一致
        hidden.UnionWith(p.Contributions.Where(c => c is not null).SelectMany(c => c.FieldAccess).Where(a => a is { Access: FieldAccess.Hidden }).Select(a => a.Field));

        // ---- events ----
        var eventCodes = new HashSet<string>();
        var effectCodes = new HashSet<string>();   // "事件代號.效果代號"
        for (var i = 0; i < p.Events.Count; i++)
        {
            var e = p.Events[i];
            var at = $"events[{i}]";
            if (e is null) { errors.Add($"{at}: 不能是 null"); continue; }
            if (!AppId().IsMatch(e.Code ?? "")) errors.Add($"{at}.code: 格式錯誤（小寫英文、數字、連字號，2 到 40 字，英文開頭）");
            else if (!eventCodes.Add(e.Code!)) errors.Add($"{at}.code: 事件代號重複 {e.Code}");
            CheckText(e.Name, $"{at}.name", errors);
            if (e.Trigger is null) { errors.Add($"{at}.trigger: 缺少觸發設定"); continue; }
            if (e.Effects.Count == 0) errors.Add($"{at}.effects: 至少要有一個效果");
            if (e.Effects.Any(x => x is null)) { errors.Add($"{at}.effects: 不能有 null"); continue; }
            if (e.Effects.Any(x => x.Kind == "custom")) errors.Add($"{at}.effects: {AppEffectsCodec.CustomMessage}");
            foreach (var x in e.Effects)
                if (!effectCodes.Add($"{e.Code}.{x.Code}")) errors.Add($"{at}.effects: 效果代號重複 {x.Code}");
            if (e.Trigger.On == "transition" && (e.Trigger.From?.Count > 0 || e.Trigger.To is not null))
            {
                if (!workflowOf.TryGetValue(e.Source ?? "", out var wf)) errors.Add($"{at}.trigger: 來源模組 {e.Source} 沒有狀態機");
                else
                    foreach (var s in (e.Trigger.From ?? []).Append(e.Trigger.To).Where(s => s is not null && !wf.States.Any(x => x.Option == s)))
                        errors.Add($"{at}.trigger: {s} 不是 {e.Source} 狀態機裡的狀態");
            }
            foreach (var w in e.Trigger.Watch ?? [])
                if (!fields.ContainsKey(w ?? "") || SplitQualified(w!)!.Value.Module != e.Source) errors.Add($"{at}.trigger.watch: {w} 必須是來源模組 {e.Source} 的欄位");

            if (e.Source is null || !modules.ContainsKey(e.Source)) { errors.Add($"{at}.source: 模組 {e.Source} 不在套件內"); continue; }
            foreach (var error in AppEffectsCodec.ToInput(e with { Effects = e.Effects.Where(x => x.Kind != "custom").ToList() }, lookups).Errors)
                errors.Add($"{at}: {error}");

            foreach (var fx in e.Effects)
                foreach (var s in fx.Set)
                {
                    if (s is null || s.AllowHiddenSource) continue;
                    var used = new List<string>();
                    if (s.From is { } from) used.Add(from.Field);
                    if (s.Formula is { } formula)
                        used.AddRange(System.Text.RegularExpressions.Regex.Matches(formula.Expr ?? "", @"\{([^{}.\s]+\.[^{}.\s]+)\}").Select(m => m.Groups[1].Value));
                    foreach (var name in used.Where(hidden.Contains).Distinct())
                        errors.Add($"{at}.effects[{fx.Code}]: 來源欄位 {name} 對某些群組設為隱藏或限定可見，複製會把看不到的值帶到目標模組，必須在該對應明確允許（allowHiddenSource）");
                }
        }
        foreach (var e in p.Events.Where(e => e is not null))
            foreach (var fx in e.Effects.Where(x => x is { Kind: "reverse" }))
                if (!effectCodes.Contains($"{fx.OfEvent}.{fx.OfEffect}")) errors.Add($"events[{e.Code}]: 沖銷的 {fx.OfEvent}.{fx.OfEffect} 不在套件內");

        if (errors.Count == 0 && EventCycle(p, workflowOf) is { } cycle)
            errors.Add($"events: 事件之間會互相觸發形成循環（{string.Join(" → ", cycle)}）");

        // ---- checks ----
        var checkCodes = new HashSet<string>();
        for (var i = 0; i < p.Checks.Count; i++)
        {
            var c = p.Checks[i];
            var at = $"checks[{i}]";
            if (c is null) { errors.Add($"{at}: 不能是 null"); continue; }
            if (!AppId().IsMatch(c.Code ?? "")) errors.Add($"{at}.code: 格式錯誤（小寫英文、數字、連字號，2 到 40 字，英文開頭）");
            else if (!checkCodes.Add(c.Code!)) errors.Add($"{at}.code: 檢查代號重複 {c.Code}");
            CheckText(c.Name, $"{at}.name", errors);
            if (c.Assert?.Limit is null) { errors.Add($"{at}.assert: 缺少上限設定"); continue; }
            foreach (var error in AppEffectsCodec.ToInput(c, lookups).Errors) errors.Add($"{at}: {error}");
        }

        // ---- ledgers ----
        var ledgerModules = new HashSet<string>();
        for (var i = 0; i < p.Ledgers.Count; i++)
        {
            var g = p.Ledgers[i];
            var at = $"ledgers[{i}]";
            if (g is null) { errors.Add($"{at}: 不能是 null"); continue; }
            if (!ledgerModules.Add(g.Module ?? "")) errors.Add($"{at}: 模組 {g.Module} 已有分錄設定");
            if (g.Swap.Any(pair => pair is null || pair.Count != 2)) { errors.Add($"{at}.swap: 每組必須剛好兩個欄位"); continue; }
            foreach (var error in AppEffectsCodec.ToInput(g, lookups).Errors) errors.Add($"{at}: {error}");
        }
        foreach (var error in masterErrors.Distinct()) errors.Add($"events／checks: {error}");
    }

    /// <summary>套件內啟用事件的環（模組層級、偏保守，和設計器同一套 <see cref="EventGraph"/>）；沒有回 null。</summary>
    private static List<string>? EventCycle(AppPackageData p, Dictionary<string, AppWorkflow> workflowOf)
    {
        var ids = new Dictionary<string, long>();
        long Id(string name) => ids.TryGetValue(name, out var id) ? id : ids[name] = ids.Count + 1;
        var nodes = new List<EventGraph.Node>();
        foreach (var e in p.Events.Where(e => e is { Active: true }))
        {
            var writes = new List<EventGraph.Write>();
            foreach (var fx in e.Effects.Where(x => x.Target is not null))
            {
                if (fx.Kind is "create" or "reverse") { writes.Add(new EventGraph.Write(fx.Target!, true, [], false)); continue; }
                var set = fx.Set.Select(s => Id(s.To)).ToHashSet();
                var touchesStatus = workflowOf.TryGetValue(fx.Target!, out var wf) && fx.Set.Any(s => s.To == Qualify(fx.Target!, wf.StatusField));
                writes.Add(new EventGraph.Write(fx.Target!, false, set, touchesStatus));
            }
            nodes.Add(new EventGraph.Node(e.Code, e.Source, new EventTrigger(e.Trigger.On, null, null, e.Trigger.Watch?.Select(Id).ToList()), writes));
        }
        foreach (var n in nodes)
            if (EventGraph.FindCycleThrough(n.Key, nodes) is { } cycle) return cycle;
        return null;
    }
}
