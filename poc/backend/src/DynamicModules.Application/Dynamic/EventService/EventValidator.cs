using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Common;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Formulas;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.EventService;

/// <summary>事件定義的結構驗證（設計 §4.3）；錯誤鍵用 definition.* 路徑。</summary>
public class EventValidator(
    IModuleRepository modules, IEventRepository events, ILedgerRepository ledgers, IPermissionRepository permissions,
    IEffectHandlerRegistry handlers, FieldTypeRegistry registry)
{
    public const int MaxEffects = 10, MaxSets = 50, MaxEventsPerModule = 20;
    public const string BadKey = "事件代碼只能用小寫英文、數字、連字號，2 到 40 字，英文開頭";
    private static readonly Regex CodePattern = new("^[a-z][a-z0-9-]{0,39}$");

    public async Task<ServiceResult?> ValidateAsync(EventInput input, CancellationToken ct) =>
        (await ValidateCoreAsync(input, ct)).Result;

    /// <summary>驗證並回傳警告（不擋的提醒）。通過時 Result 為 null。</summary>
    public async Task<(ServiceResult? Result, List<string> Warnings)> ValidateCoreAsync(EventInput input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var warnings = new List<string>();
        void Err(string key, string message) => errors.TryAdd(key, [message]);

        if (!Regex.IsMatch(input.Key ?? "", ModuleRules.ModuleKeyPattern)) Err("key", BadKey);
        if (input.Name is null || !input.Name.IsValid(out _)) Err("name", "名稱必須有繁體中文");
        if (input.Definition?.Trigger is null || input.Definition.Effects is null)
            return (ServiceResult.Invalid(new Dictionary<string, string[]> { ["definition"] = ["缺少事件定義"] }), warnings);

        var source = await modules.GetByKeyAsync(input.SourceModuleKey ?? "", includeChildren: true, ct);
        if (source is null) Err("sourceModuleKey", "來源模組不存在");
        else if (!source.IsWritable() || source.Kind != ModuleKind.Standalone || ModuleRelations.IsDetail(source))
            Err("sourceModuleKey", "來源必須是啟用中的獨立模組，不能是明細或擴充模組");
        if (errors.Count > 0 || source is null) return (ServiceResult.Invalid(errors), warnings);

        var def = input.Definition;
        CheckTrigger(source, def.Trigger, errors);
        await CheckWhenAsync(source, def.When, errors, ct);

        if (def.Effects.Count is 0 or > MaxEffects) Err("definition.effects", $"效果至少 1 個、至多 {MaxEffects} 個");
        var codes = new HashSet<string>();
        for (var i = 0; i < def.Effects.Count && i < MaxEffects; i++)
        {
            var fx = def.Effects[i];
            var path = $"definition.effects[{i}]";
            if (!CodePattern.IsMatch(fx.Code ?? "")) Err($"{path}.code", "效果代碼只能用小寫英文、數字、連字號，英文開頭，至多 40 字");
            else if (!codes.Add(fx.Code!)) Err($"{path}.code", "效果代碼在事件內不能重複");
            await CheckEffectAsync(input, source, fx, path, errors, warnings, ct);
        }

        if (errors.Count == 0)
        {
            var count = (await events.ListBySourceAsync(source.Id, ct)).Count(e => e.Key != input.Key);
            if (count >= MaxEventsPerModule) Err("sourceModuleKey", $"每個來源模組至多 {MaxEventsPerModule} 個事件");
        }
        if (errors.Count == 0 && input.Active && await FindCycleAsync(input, source, ct) is { } cycle)
            Err("definition", $"連動事件形成循環：{string.Join(" → ", cycle)}");
        return (errors.Count > 0 ? ServiceResult.Invalid(errors) : null, warnings);
    }

    private static void CheckTrigger(Module source, EventTrigger t, Dictionary<string, string[]> errors)
    {
        void Err(string key, string message) => errors.TryAdd(key, [message]);
        var fieldIds = source.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).Select(f => f.Id).ToHashSet();
        switch (t.On)
        {
            case "create": break;
            case "update":
                if (t.Watch?.Any(id => !fieldIds.Contains(id)) == true) Err("definition.trigger.watch", "監看的欄位必須是來源模組的啟用欄位");
                break;
            case "transition":
                if (source.Workflow is null) { Err("definition.trigger.on", "狀態轉移觸發需要來源模組有狀態機"); break; }
                var states = source.Workflow.States.Select(s => s.OptionId).ToHashSet();
                if (t.ToState is not { } to || !states.Contains(to)) Err("definition.trigger.toState", "目標狀態必須是來源模組狀態機裡的狀態");
                if (t.FromStates?.Any(s => !states.Contains(s)) == true) Err("definition.trigger.fromStates", "來源狀態必須是來源模組狀態機裡的狀態");
                break;
            default: Err("definition.trigger.on", "觸發種類必須是 create、update 或 transition"); break;
        }
    }

    private async Task CheckWhenAsync(Module source, JsonElement? when, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        if (when is null or { ValueKind: JsonValueKind.Null }) return;
        if (when.Value.ValueKind != JsonValueKind.Object) { errors.TryAdd("definition.when", ["條件格式錯誤"]); return; }
        var parsed = ConditionParser.Parse(when.Value, source, registry, ConditionUsage.Rule, await modules.ListUserIdsAsync(ct));
        if (parsed.Error is not null) errors.TryAdd("definition.when", [$"條件不正確：{parsed.Error}"]);
    }

    private async Task CheckEffectAsync(
        EventInput input, Module source, EventEffect fx, string path,
        Dictionary<string, string[]> errors, List<string> warnings, CancellationToken ct)
    {
        void Err(string key, string message) => errors.TryAdd(key, [message]);
        if (fx.Kind == "custom")
        {
            var handler = handlers.Find(fx.Handler);
            if (handler is null) { Err($"{path}.handler", "找不到擴充處理器"); return; }
            foreach (var e in handler.ValidateArgs(fx.Args)) Err($"{path}.args", e);
            return;
        }
        if (fx.Kind is not ("create" or "update" or "reverse")) { Err($"{path}.kind", "效果種類必須是 create、update、reverse 或 custom"); return; }

        var target = await modules.GetByKeyAsync(fx.TargetModuleKey ?? "", includeChildren: true, ct);
        if (target is null || !target.IsWritable() || target.Kind != ModuleKind.Standalone || ModuleRelations.IsDetail(target))
        {
            Err($"{path}.targetModuleKey", "目標必須是啟用中的獨立模組，不能是明細或擴充模組");
            return;
        }

        if (fx.Kind == "reverse")
        {
            if (await ledgers.GetByModuleAsync(target.Id, ct) is null) { Err($"{path}.targetModuleKey", "沖銷的目標必須是分錄模組"); return; }
            await CheckReverseRefAsync(input, fx, target, path, errors, ct);
            return;
        }

        Module? eachModule = null;
        if (fx.Kind == "create" && !string.IsNullOrEmpty(fx.Each))
        {
            eachModule = await modules.GetByKeyAsync(fx.Each, includeChildren: true, ct);
            if (eachModule is null || ModuleRelations.ParentKeyOf(eachModule) != source.Key)
            {
                Err($"{path}.each", "必須是來源模組的明細模組");
                return;
            }
        }
        if (fx.Kind == "update")
        {
            var via = source.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == fx.Via && f.Status == FieldStatus.Active);
            if (via is not { Type: FieldType.Reference } || via.Config.RefTarget is not { Kind: ReferenceTarget.ModuleKind } rt || rt.Key != target.Key
                || via.Config.Relation == ReferenceRelation.Detail)
                Err($"{path}.via", "必須是來源模組上、指向目標模組的參照欄位");
        }

        await CheckSetsAsync(source, eachModule, target, fx, path, errors, warnings, ct);
    }

    private async Task CheckReverseRefAsync(EventInput input, EventEffect fx, Module target, string path, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        void Err(string key, string message) => errors.TryAdd(key, [message]);
        EventDefinition? of = null;
        if (fx.OfEvent == input.Key) of = input.Definition;
        else if (!string.IsNullOrEmpty(fx.OfEvent) && await events.GetByKeyAsync(fx.OfEvent, ct) is { } e) of = e.Definition;
        var ofEffect = of?.Effects.FirstOrDefault(x => x.Code == fx.OfEffect);
        if (ofEffect is not { Kind: "create" } || ofEffect.TargetModuleKey != target.Key)
            Err($"{path}.ofEffect", "必須指向某個事件裡、目標相同的新增效果");
    }

    private async Task CheckSetsAsync(
        Module source, Module? each, Module target, EventEffect fx, string path,
        Dictionary<string, string[]> errors, List<string> warnings, CancellationToken ct)
    {
        void Err(string key, string message) => errors.TryAdd(key, [message]);
        var sets = fx.Set ?? [];
        if (sets.Count > MaxSets) { Err($"{path}.set", $"每個效果至多 {MaxSets} 個對應"); return; }
        var targetFields = target.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).ToDictionary(f => f.Id);
        var sourceFields = source.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).ToDictionary(f => f.Id);
        var lineFields = each?.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).ToDictionary(f => f.Id);
        var hiddenSource = await HiddenAsync(source, ct);
        var hiddenLine = each is null ? [] : await HiddenAsync(each, ct);
        var seen = new HashSet<long>();

        for (var j = 0; j < sets.Count; j++)
        {
            var s = sets[j];
            var sp = $"{path}.set[{j}]";
            if (!targetFields.TryGetValue(s.To, out var dst)) { Err($"{sp}.to", "目的欄位必須是目標模組的啟用欄位"); continue; }
            if (!seen.Add(s.To)) Err($"{sp}.to", "目的欄位不能重複");
            if (FieldRules.IsComputed(dst.Type) || dst.Type == FieldType.Reference && dst.Config.Relation == ReferenceRelation.Detail)
                Err($"{sp}.to", "目的欄位不能是公式、彙總、自動編號或明細參照欄位");

            var kinds = (s.From is not null ? 1 : 0) + (s.Formula is not null ? 1 : 0) + (s.Const is not null ? 1 : 0);
            if (kinds != 1) { Err(sp, "每個對應必須且只能指定 from、formula、const 其中之一"); continue; }

            if (s.From is { } from)
            {
                var fields = SideFields(from.Side, fx, sourceFields, lineFields, out var hidden, hiddenSource, hiddenLine);
                if (fields is null) { Err($"{sp}.from.side", "side 必須是 header，或有 each 的 line"); continue; }
                if (!fields.TryGetValue(from.FieldId, out var src)) { Err($"{sp}.from.fieldId", "來源欄位不屬於該邊的模組"); continue; }
                if (fx.Kind == "update" && from.Side != "header") Err($"{sp}.from.side", "update 效果只能用 header");
                if (!FieldRules.CopyCompatible(src, dst, allowReference: true)) Err($"{sp}.from", "來源與目的欄位型別不相容");
                if (hidden.Contains(src.Id) && !s.AllowHiddenSource) Err($"{sp}.allowHiddenSource", HiddenMessage(src));
                if (ModuleRelations.DependsOnRollup(from.Side == "line" ? each! : source, src)) Err($"{sp}.from.fieldId", RollupMessage(src));
            }
            else if (s.Formula is { } formula)
            {
                var fields = SideFields(formula.Side, fx, sourceFields, lineFields, out var hidden, hiddenSource, hiddenLine);
                var module = formula.Side == "line" ? each : source;
                if (fields is null || module is null) { Err($"{sp}.formula.side", "side 必須是 header，或有 each 的 line"); continue; }
                var parsed = FormulaParser.Parse(formula.Expr, module);
                if (parsed.Error is not null) { Err($"{sp}.formula", $"公式不正確：{parsed.Error}"); continue; }
                var numeric = dst.Type is FieldType.Integer or FieldType.Decimal or FieldType.Currency or FieldType.Percent;
                if (parsed.Type == FormulaValueType.Date ? dst.Type != FieldType.Date : !numeric)
                    Err($"{sp}.formula", "公式結果型別與目的欄位不相容");
                if (!s.AllowHiddenSource && parsed.FieldRefs.FirstOrDefault(hidden.Contains) is var h and not 0)
                    Err($"{sp}.allowHiddenSource", HiddenMessage(fields[h]));
                if (parsed.FieldRefs.Where(fields.ContainsKey).Select(id => fields[id]).FirstOrDefault(f => ModuleRelations.DependsOnRollup(module, f)) is { } rolled)
                    Err($"{sp}.formula", RollupMessage(rolled));
            }
            else if (s.Const is { ValueKind: JsonValueKind.String } c && c.GetString() == "today" && dst.Type is not FieldType.Date)
                Err($"{sp}.const", "\"today\" 只能用在日期欄位");
        }

        if (fx.Kind == "create")
        {
            var covered = sets.Select(s => s.To).ToHashSet();
            foreach (var f in targetFields.Values.Where(f => f.Config is { Required: true, DefaultValue: null, DefaultToday: false } && !FieldRules.IsComputed(f.Type) && !covered.Contains(f.Id)))
                warnings.Add($"效果 {fx.Code}：目標模組的必填欄位「{f.LabelI18n.Resolve("zh-TW")}」沒有對應也沒有預設值");
        }
    }

    private static string RollupMessage(Field f) =>
        $"來源欄位「{f.LabelI18n.Resolve("zh-TW")}」是彙總欄位或依賴彙總的公式；彙總會隨子記錄變動，複製後與已記帳的帳務失配，不能當來源";

    private static string HiddenMessage(Field f) =>
        $"來源欄位「{f.LabelI18n.Resolve("zh-TW")}」對某些群組設為隱藏或限定可見；複製會把看不到的值帶到目標模組，必須在該對應明確允許（allowHiddenSource）";

    private static Dictionary<long, Field>? SideFields(
        string side, EventEffect fx, Dictionary<long, Field> sourceFields, Dictionary<long, Field>? lineFields,
        out HashSet<long> hidden, HashSet<long> hiddenSource, HashSet<long> hiddenLine)
    {
        hidden = side == "line" ? hiddenLine : hiddenSource;
        if (side == "header") return sourceFields;
        return side == "line" && fx.Kind == "create" ? lineFields : null;
    }

    /// <summary>
    /// 對某些群組看不到的來源欄位：設為 Hidden 的欄位，加上限定可見的貢獻欄位（共用主檔 §6）與引用它們的公式（逐層），
    /// 後兩者和 PermissionResolver 算 hidden 的規則一致（審查必修 4）。
    /// </summary>
    public async Task<HashSet<long>> HiddenAsync(Module m, CancellationToken ct)
    {
        var hidden = (await permissions.LoadModuleRowsAsync(m, ct)).FieldPermissions.Where(p => p.Access == FieldAccess.Hidden).Select(p => p.FieldId).ToHashSet();
        var restricted = m.Blocks.SelectMany(b => b.Fields).Where(f => f.VisibilityRestricted).Select(f => f.Id).ToHashSet();
        if (restricted.Count == 0) return hidden;
        foreach (var f in FormulaGraph.EvaluationOrder(m))
            if (FormulaGraph.RefsOf(f).Overlaps(restricted)) restricted.Add(f.Id);
        hidden.UnionWith(restricted);
        return hidden;
    }

    /// <summary>此事件（啟用時）加入既有的啟用事件後，圖上是否有包含它的環。</summary>
    private async Task<List<string>?> FindCycleAsync(EventInput input, Module source, CancellationToken ct)
    {
        var all = (await events.ListAsync(ct)).Where(e => e.Active && e.Key != input.Key).ToList();
        var sourceKeys = await SourceKeysAsync(all, ct);
        var nodes = new List<EventGraph.Node>();
        foreach (var e in all)
            nodes.Add(await NodeAsync(e.Key, sourceKeys[e.SourceModuleId], e.Definition, ct));
        nodes.Add(await NodeAsync(input.Key, source.Key, input.Definition, ct));
        return EventGraph.FindCycleThrough(input.Key, nodes);
    }

    private async Task<Dictionary<long, string>> SourceKeysAsync(List<ModuleEvent> all, CancellationToken ct)
    {
        var result = new Dictionary<long, string>();
        foreach (var id in all.Select(e => e.SourceModuleId).Distinct())
            if (await modules.GetByIdAsync(id, includeChildren: false, ct) is { } m) result[id] = m.Key;
        return result;
    }

    /// <summary>把事件轉成圖節點。公開給執行期與套件開啟使用，同一套判斷。</summary>
    public async Task<EventGraph.Node> NodeAsync(string key, string sourceKey, EventDefinition def, CancellationToken ct)
    {
        var writes = new List<EventGraph.Write>();
        foreach (var fx in def.Effects)
        {
            if (fx.Kind == "custom")
            {
                foreach (var w in handlers.Find(fx.Handler)?.DeclareWrites(fx.Args) ?? [])
                    writes.Add(new EventGraph.Write(w.ModuleKey, w.Create, [], w.TouchesStatus));
                continue;
            }
            if (fx.TargetModuleKey is not { } targetKey) continue;
            if (fx.Kind is "create" or "reverse") { writes.Add(new EventGraph.Write(targetKey, true, [], false)); continue; }
            var target = await modules.GetByKeyAsync(targetKey, includeChildren: true, ct);
            var set = (fx.Set ?? []).Select(s => s.To).ToHashSet();
            // 設了公式的輸入欄位，同一筆記錄的公式欄位跟著變，監看公式欄位的 update 事件也可能被觸發
            var changed = target is null ? set : WithDependentFormulas(target, set);
            writes.Add(new EventGraph.Write(targetKey, false, changed, target?.Workflow is { } wf && set.Contains(wf.StatusFieldId)));
        }
        return new EventGraph.Node(key, sourceKey, def.Trigger, writes);
    }

    private static HashSet<long> WithDependentFormulas(Module target, HashSet<long> set)
    {
        var result = new HashSet<long>(set);
        var formulas = target.Blocks.SelectMany(b => b.Fields).Where(f => f is { Type: FieldType.Formula, Status: FieldStatus.Active }).ToList();
        bool grew;
        do
        {
            grew = false;
            foreach (var f in formulas)
                if (!result.Contains(f.Id) && FormulaGraph.RefsOf(f).Any(result.Contains)) { result.Add(f.Id); grew = true; }
        } while (grew);
        return result;
    }
}
