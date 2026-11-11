using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.LedgerService;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// 連動事件、跨記錄檢查、分錄設定在套件（代號）與資料庫（Id）之間互轉。
/// 解析不了的引用累積成錯誤，不丟例外也不猜；呼叫端決定是整包拒絕（驗證）、略過並警告（開啟時欄位被排除）或不匯出（匯出）。
/// </summary>
public static partial class AppEffectsCodec
{
    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex IdReference();

    [GeneratedRegex(@"\{([^{}.\s]+\.[^{}.\s]+)\}")]
    private static partial Regex NameReference();

    [GeneratedRegex(@"^opt_(.+)$")]
    private static partial Regex OptionValue();

    public const string ConcreteRecordMessage = "條件指定了具體記錄或使用者帳號，換一個資料庫會指到別的對象，套件不收這種條件";
    public const string ConstantRecordMessage = "事件對參照或使用者欄位設定了固定的記錄或帳號，換一個資料庫會指到別的對象，套件不收";
    public const string CustomMessage = "自訂效果的參數是外掛專屬的，無法轉成代號，套件不收";

    // ---- 匯出方向：Id → 代號 ----

    /// <summary>模組鍵 → 模組代號，加上 <see cref="AppReportCodec.Names"/> 的欄位與選項查表。</summary>
    public static (AppEvent? Event, List<string> Errors) ToPackage(
        ModuleEvent e, Module source, Func<string, string?> moduleCode, AppReportCodec.Names n)
    {
        var errors = new List<string>();
        string Module(string? key)
        {
            var code = key is null ? null : moduleCode(key);
            if (code is null) errors.Add($"引用了套件外的模組 {key}");
            return code ?? "";
        }
        string Field(long id)
        {
            var name = n.Field(id);
            if (name is null) errors.Add("引用了套件外或已停用的欄位");
            return name ?? "";
        }
        string? State(long? id) =>
            id is null ? null : source.Workflow is { } w && n.Option(w.StatusFieldId, id.Value) is { } code
                ? code : Fail<string>("狀態機的狀態未匯出");
        T? Fail<T>(string message) { errors.Add(message); return default; }

        var d = e.Definition;
        var trigger = new AppEventTrigger(d.Trigger.On, d.Trigger.FromStates?.Select(s => State(s)!).ToList(), State(d.Trigger.ToState),
            d.Trigger.Watch?.Select(Field).ToList());
        var effects = new List<AppEffect>();
        foreach (var fx in d.Effects)
        {
            if (fx.Kind == "custom") { errors.Add($"{CustomMessage}（效果 {fx.Code}）"); continue; }
            foreach (var s in fx.Set ?? [])
                if (s.Const is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } && n.IdentityTypeOf(s.To) is not null)
                    errors.Add($"{ConstantRecordMessage}（效果 {fx.Code}）");
            var set = (fx.Set ?? []).Select(s => new AppEventSet(
                Field(s.To),
                s.From is { } f ? new AppEventFrom(f.Side, Field(f.FieldId)) : null,
                s.Formula is { } fo ? new AppEventFormula(fo.Side, IdReference().Replace(fo.Expr ?? "", m => $"{{{Field(long.Parse(m.Groups[1].Value))}}}")) : null,
                s.Const is { } c ? AppReportCodec.ExportValue(c, s.To, n) : null,
                s.AllowHiddenSource)).ToList();
            effects.Add(new AppEffect(fx.Code, fx.Kind, fx.TargetModuleKey is null ? null : Module(fx.TargetModuleKey),
                string.IsNullOrEmpty(fx.Each) ? null : Module(fx.Each), fx.Via is { } via ? Field(via) : null, set, fx.OfEvent, fx.OfEffect));
        }
        var package = new AppEvent(e.Key, e.NameI18n, e.Active, Module(source.Key), trigger,
            d.When is { } when ? ExportCondition(when, n, errors) : null, effects);
        return (errors.Count == 0 ? package : null, errors.Distinct().ToList());
    }

    public static (AppCheck? Check, List<string> Errors) ToPackage(
        ModuleCheck c, string moduleCode, AppReportCodec.Names n)
    {
        var errors = new List<string>();
        string Field(long id)
        {
            var name = n.Field(id);
            if (name is null) errors.Add("引用了套件外或已停用的欄位");
            return name ?? "";
        }
        var d = c.Definition;
        var limit = d.Assert.Limit;
        var package = new AppCheck(c.Key, c.NameI18n, c.Active, moduleCode, d.GroupBy.Select(Field).ToList(), d.Function,
            d.FieldId is { } f ? Field(f) : null, d.Where is { } w ? ExportCondition(w, n, errors) : null,
            new AppCheckAssert(d.Assert.Op, new AppCheckLimit(limit.Const, limit.Field is { } lf ? Field(lf) : null, limit.Via is { } lv ? Field(lv) : null)),
            d.Message);
        return (errors.Count == 0 ? package : null, errors.Distinct().ToList());
    }

    public static (AppLedger? Ledger, List<string> Errors) ToPackage(ModuleLedger l, string moduleCode, AppReportCodec.Names n)
    {
        var errors = new List<string>();
        string Field(long id)
        {
            var name = n.Field(id);
            if (name is null) errors.Add("引用了套件外或已停用的欄位");
            return name ?? "";
        }
        var d = l.Definition;
        var package = new AppLedger(moduleCode, Field(d.ReversalFieldId), d.Negate.Select(Field).ToList(),
            d.Swap.Select(pair => pair.Select(Field).ToList()).ToList());
        return (errors.Count == 0 ? package : null, errors.Distinct().ToList());
    }

    private static JsonElement ExportCondition(JsonElement json, AppReportCodec.Names n, List<string> errors)
    {
        JsonNode? Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonArray array:
                    return new JsonArray(array.Select(Walk).ToArray());
                case JsonObject obj:
                    var copy = new JsonObject();
                    long fieldId = 0;
                    foreach (var (key, value) in obj)
                    {
                        if (key is "field" or "fieldRef" && value is JsonValue v && v.TryGetValue<long>(out var id))
                        {
                            if (key == "field") fieldId = id;
                            var name = n.Field(id);
                            if (name is null) errors.Add("條件引用了套件外或已停用的欄位");
                            copy[key] = name;
                        }
                        else copy[key] = Walk(value);
                    }
                    if (fieldId != 0 && copy["value"] is { } raw)
                    {
                        var op = obj["op"]?.GetValue<string>() ?? "";
                        if (n.IdentityTypeOf(fieldId) is not null && AppReportCodec.PicksConcreteRecord(op, JsonSerializer.SerializeToElement(raw)))
                            errors.Add(ConcreteRecordMessage);
                        copy["value"] = JsonNode.Parse(AppReportCodec.ExportValue(JsonSerializer.SerializeToElement(raw), fieldId, n).GetRawText());
                    }
                    return copy;
                default:
                    return node?.DeepClone();
            }
        }
        return JsonSerializer.SerializeToElement(Walk(JsonNode.Parse(json.GetRawText())));
    }

    // ---- 匯入方向：代號 → Id ----

    /// <summary>
    /// 把套件的事件轉成設計器的輸入。任何引用解析不到（欄位被取消勾選等）就回 null 並列出原因。
    /// Lookups 的 FieldId、OptionId 回 null 代表找不到；TypeOf 用來判斷哪些欄位的值是選項。
    /// </summary>
    public static (EventInput? Input, List<string> Errors) ToInput(AppEvent e, AppReportCodec.Lookups l)
    {
        var errors = new List<string>();
        string? Module(string? code)
        {
            var key = code is null ? null : l.ModuleKey(code);
            if (key is null) errors.Add($"引用了套件沒有的模組 {code}");
            return key;
        }
        long Field(string? name)
        {
            var id = name is null ? null : l.FieldId(name);
            if (id is null) errors.Add($"引用了套件沒有的欄位 {name}");
            return id ?? 0;
        }
        long? State(string? code)
        {
            if (code is null) return null;
            var id = l.OptionId($"{e.Source}.{code}");
            if (id is null) errors.Add($"引用了套件沒有的狀態 {code}");
            return id;
        }

        var trigger = new EventTrigger(e.Trigger.On, e.Trigger.From?.Select(s => State(s) ?? 0).ToList(), State(e.Trigger.To),
            e.Trigger.Watch?.Select(Field).ToList());
        var effects = e.Effects.Select(fx => new EventEffect(
            fx.Code, fx.Kind, fx.Target is null ? null : Module(fx.Target), fx.Each is null ? null : Module(fx.Each),
            fx.Via is null ? null : Field(fx.Via),
            fx.Set.Select(s =>
            {
                var to = Field(s.To);
                if (s.Const is { } constant)
                {
                    if (AppReportCodec.IsIdentityType(l.TypeOf(s.To)) && constant.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                        errors.Add($"{ConstantRecordMessage}（效果 {fx.Code}）");
                    if (OptionModule(s.To, l) is { } module) CheckOptions(constant, module, l, errors);
                }
                return new EventSet(to,
                    s.From is { } f ? new EventFrom(f.Side, Field(f.Field)) : null,
                    s.Formula is { } fo ? new EventFormula(fo.Side, NameReference().Replace(fo.Expr ?? "", m => $"{{{Field(m.Groups[1].Value)}}}")) : null,
                    s.Const is { } c ? AppReportCodec.ConvertValue(c, OptionModule(s.To, l), l.OptionId) : null,
                    s.AllowHiddenSource);
            }).ToList(),
            fx.OfEvent, fx.OfEffect, null, null)).ToList();
        var definition = new EventDefinition(trigger, e.When is { } w ? ImportCondition(w, l, errors) : null, effects);
        var sourceKey = Module(e.Source);
        return errors.Count == 0 && sourceKey is not null
            ? (new EventInput(e.Code, e.Name, sourceKey, e.Active, definition), [])
            : (null, errors.Distinct().ToList());
    }

    public static (CheckInput? Input, List<string> Errors) ToInput(AppCheck c, AppReportCodec.Lookups l)
    {
        var errors = new List<string>();
        long Field(string? name)
        {
            var id = name is null ? null : l.FieldId(name);
            if (id is null) errors.Add($"引用了套件沒有的欄位 {name}");
            return id ?? 0;
        }
        var moduleKey = l.ModuleKey(c.Module);
        if (moduleKey is null) errors.Add($"引用了套件沒有的模組 {c.Module}");
        var limit = c.Assert.Limit;
        var definition = new CheckDefinition(c.GroupBy.Select(Field).ToList(), c.Function, c.Field is null ? null : Field(c.Field),
            c.Where is { } w ? ImportCondition(w, l, errors) : null,
            new CheckAssert(c.Assert.Op, new CheckLimit(limit.Const, limit.Field is null ? null : Field(limit.Field), limit.Via is null ? null : Field(limit.Via))),
            c.Message);
        return errors.Count == 0 && moduleKey is not null
            ? (new CheckInput(c.Code, c.Name, moduleKey, c.Active, definition), [])
            : (null, errors.Distinct().ToList());
    }

    public static (LedgerInput? Input, List<string> Errors) ToInput(AppLedger g, AppReportCodec.Lookups l)
    {
        var errors = new List<string>();
        long Field(string? name)
        {
            var id = name is null ? null : l.FieldId(name);
            if (id is null) errors.Add($"引用了套件沒有的欄位 {name}");
            return id ?? 0;
        }
        var input = new LedgerInput(Field(g.ReversalField), g.Negate.Select(Field).ToList(), g.Swap.Select(p => p.Select(Field).ToList()).ToList(), false);
        return errors.Count == 0 ? (input, []) : (null, errors.Distinct().ToList());
    }

    /// <summary>只有選項型欄位的值才做 opt_ 對應；文字欄位剛好填了 opt_xxx 是字面值。</summary>
    private static string? OptionModule(string qualifiedField, AppReportCodec.Lookups l) =>
        AppReportCodec.IsOptionType(l.TypeOf(qualifiedField)) ? TemplateQualified.ModuleOf(qualifiedField) : null;

    /// <summary>選項型欄位的 opt_ 值在套件裡找不到對應選項：ConvertValue 會原樣留下，到首次觸發才 400，所以匯入時就拒絕。</summary>
    private static void CheckOptions(JsonElement value, string module, AppReportCodec.Lookups l, List<string> errors)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) CheckOptions(item, module, l, errors);
                break;
            case JsonValueKind.String when OptionValue().Match(value.GetString()!) is { Success: true } m && l.OptionId($"{module}.{m.Groups[1].Value}") is null:
                errors.Add($"引用了套件沒有的選項 {m.Groups[1].Value}（模組 {module}）");
                break;
        }
    }

    internal static JsonElement ImportCondition(JsonElement json, AppReportCodec.Lookups l, List<string> errors)
    {
        JsonNode? Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonArray array:
                    return new JsonArray(array.Select(Walk).ToArray());
                case JsonObject obj:
                    var copy = new JsonObject();
                    string? fieldName = null;
                    foreach (var (key, value) in obj)
                    {
                        if (key is "field" or "fieldRef" && value is JsonValue v && v.TryGetValue<string>(out var name))
                        {
                            if (key == "field") fieldName = name;
                            if (l.FieldId(name) is { } id) copy[key] = id;
                            else errors.Add($"條件引用了套件沒有的欄位 {name}");
                        }
                        else copy[key] = Walk(value);
                    }
                    if (fieldName is not null && copy["value"] is { } raw)
                    {
                        var value = JsonSerializer.SerializeToElement(raw);
                        // 指定哪筆記錄或哪個帳號的條件：換資料庫會指到別人，匯出時就不收；匯入時也要擋，不然開了之後再匯出整條檢查消失
                        if (AppReportCodec.IsIdentityType(l.TypeOf(fieldName)) && AppReportCodec.PicksConcreteRecord(obj["op"]?.GetValue<string>() ?? "", value))
                            errors.Add(ConcreteRecordMessage);
                        var optionModule = OptionModule(fieldName, l);
                        if (optionModule is not null) CheckOptions(value, optionModule, l, errors);
                        copy["value"] = JsonNode.Parse(AppReportCodec.ConvertValue(value, optionModule, l.OptionId).GetRawText());
                    }
                    return copy;
                default:
                    return node?.DeepClone();
            }
        }
        return JsonSerializer.SerializeToElement(Walk(JsonNode.Parse(json.GetRawText())));
    }
}
