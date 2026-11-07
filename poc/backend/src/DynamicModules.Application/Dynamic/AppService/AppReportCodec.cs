using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// 報表定義在套件（代號）與資料庫（Id）之間互轉。解析不了的引用一律累積成錯誤，不丟例外也不猜；
/// 開啟前的驗證用「只看存不存在」的解析器，開啟時用真正配發的 Id。
/// </summary>
public static partial class AppReportCodec
{
    [GeneratedRegex(@"^opt_(.+)$")]
    private static partial Regex OptionValue();

    /// <summary>
    /// 代號 → Id 所需的查表。ModuleKey：模組代號 → Module.Key；FieldId：限定欄位名 → 欄位 Id；OptionId：「模組代號.選項代號」→ 選項 Id；
    /// TypeOf：限定欄位名 → 欄位型別（只有選項型欄位才做 opt_ 對應，只有參照型欄位的條件值可能是具體記錄）。
    /// </summary>
    public sealed record Lookups(
        Func<string, string?> ModuleKey, Func<string, long?> FieldId, Func<string, long?> OptionId, Func<string, FieldType?> TypeOf);

    /// <summary>套件裡「模組代號.欄位代號」→ 欄位型別，給 <see cref="Lookups.TypeOf"/> 用。</summary>
    public static Func<string, FieldType?> TypesOf(IEnumerable<TemplateModule> modules)
    {
        var types = modules.SelectMany(m => m.Blocks.SelectMany(b => b.Fields).Select(f => ($"{m.Code}.{f.Code}", f.Type)))
            .ToDictionary(x => x.Item1, x => x.Type);
        return name => types.TryGetValue(name, out var type) ? type : null;
    }

    private static bool IsOptionType(FieldType? type) => type is FieldType.Enum or FieldType.MultiEnum;

    /// <summary>指定哪一筆記錄的條件（參照欄位 = / ≠ / 屬於 某個 Id）：換一個資料庫 Id 會指到別的記錄，套件不收。</summary>
    private static bool PicksConcreteRecord(string op, JsonElement? value) =>
        op is "eq" or "ne" or "in" or "notIn" && value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };

    public const string ConcreteRecordMessage = "報表條件指定了具體記錄，換一個資料庫會指到別的記錄，套件不收這種條件";

    public static (ReportDefinition? Definition, List<string> Errors) ToDefinition(AppReportDefinition d, Lookups l)
    {
        var errors = new List<string>();
        string? Module(string code)
        {
            var key = l.ModuleKey(code);
            if (key is null) errors.Add($"報表引用了套件沒有的模組 {code}");
            return key;
        }
        long Field(string qualified)
        {
            var id = l.FieldId(qualified);
            if (id is null) errors.Add($"報表引用了套件沒有的欄位 {qualified}");
            return id ?? 0;
        }
        ReportFieldRef? Ref(AppReportFieldRef? r)
        {
            if (r is null) return null;
            if (r.Field is null && r.Via is null) { errors.Add("報表欄位引用需要 field 或 via"); return null; }
            return new ReportFieldRef(r.Source, r.Field is null ? 0 : Field(r.Field), r.Via is null ? null : Field(r.Via));
        }
        ReportFilter? Filter(AppReportFilter? f)
        {
            if (f is null) return null;
            var field = Ref(f.Field);
            if (field is null) return null;
            // 值裡的 opt_<選項代號> 換成 opt_<選項 Id>：只有選項型欄位才換（文字欄位剛好填了 opt_paid 就是字面值）；欄位來源模組由限定名決定
            var type = f.Field.Field is { } name ? l.TypeOf(name) : null;
            if (type == FieldType.Reference && PicksConcreteRecord(f.Op, f.Value)) errors.Add($"{ConcreteRecordMessage}（{f.Field.Field}）");
            var module = IsOptionType(type) && f.Field.Field is { } q && TemplateQualified.ModuleOf(q) is { } m ? m : null;
            return new ReportFilter(field, f.Op, f.Value is { } v ? ConvertValue(v, module, l.OptionId) : null);
        }

        var main = Module(d.MainModule);
        var child = d.Child is { } c ? new ReportChild(Module(c.Module) ?? "", Field(c.LinkField)) : null;
        var def = new ReportDefinition(
            d.Version, main ?? "", child,
            d.Columns.Select(Ref).OfType<ReportFieldRef>().ToList(),
            d.Groups.Select(g => Ref(g.Field) is { } r ? new ReportGroup(r, g.Bucket) : null).OfType<ReportGroup>().ToList(),
            d.Measures.Select(m => new ReportMeasure(m.Id, m.Kind, Ref(m.Field), Filter(m.Condition))).ToList(),
            d.Filters.Select(Filter).OfType<ReportFilter>().ToList(),
            Ref(d.PrimaryDateField), d.DateRange,
            d.Sort.Select(s => new ReportSort(SortKey(s.Key, d, Field), s.Desc)).ToList(), d.Viz);
        return (errors.Count == 0 ? def : null, errors.Distinct().ToList());
    }

    private static string SortKey(string key, AppReportDefinition d, Func<string, long> field)
    {
        if (d.Measures.Any(m => m.Id == key)) return key;
        var parts = key.Split(':');
        if (parts.Length != 3) return key;
        var id = parts[1].Length == 0 ? 0 : field(parts[1]);
        return parts[2].Length == 0 ? $"{parts[0]}:{id}:" : $"{parts[0]}:{id}:{field(parts[2])}";
    }

    private static JsonElement ConvertValue(JsonElement value, string? module, Func<string, long?> optionId)
    {
        JsonNode? Walk(JsonNode? node) => node switch
        {
            JsonArray array => new JsonArray(array.Select(Walk).ToArray()),
            JsonValue v when module is not null && v.TryGetValue<string>(out var s) && OptionValue().Match(s) is { Success: true } match
                && optionId($"{module}.{match.Groups[1].Value}") is { } id => JsonValue.Create($"opt_{id}"),
            _ => node?.DeepClone()
        };
        return JsonSerializer.SerializeToElement(Walk(JsonNode.Parse(value.GetRawText())));
    }

    // ---- 匯出方向：Id → 代號 ----

    /// <summary>Id → 代號所需的查表。Module：Module.Key → 模組代號；Field：欄位 Id → 限定欄位名；Option：選項 Id → 選項代號（只收該欄位的選項）。</summary>
    public sealed record Names(
        Func<string, string?> Module, Func<long, string?> Field, Func<long, long, string?> Option, Func<long, bool> IsOptionField,
        Func<long, bool> IsReferenceField);

    public static (AppReportDefinition? Definition, List<string> Errors) ToPackage(ReportDefinition d, Names n)
    {
        var errors = new List<string>();
        string Module(string key)
        {
            var code = n.Module(key);
            if (code is null) errors.Add($"報表引用了套件外的模組 {key}");
            return code ?? "";
        }
        string? Field(long id)
        {
            var name = n.Field(id);
            if (name is null) errors.Add("報表引用了套件外或已停用的欄位");
            return name;
        }
        AppReportFieldRef? Ref(ReportFieldRef? r)
        {
            if (r is null) return null;
            var field = r.FieldId == 0 && r.ViaFieldId is not null ? null : Field(r.FieldId);
            return new AppReportFieldRef(r.Source, field, r.ViaFieldId is { } via ? Field(via) : null);
        }
        AppReportFilter? Filter(ReportFilter? f)
        {
            if (f is null) return null;
            var field = Ref(f.Field);
            if (field is null) return null;
            if (f.Field.FieldId != 0 && n.IsReferenceField(f.Field.FieldId) && PicksConcreteRecord(f.Op, f.Value))
                errors.Add($"{ConcreteRecordMessage}（{field.Field}）");
            return new AppReportFilter(field, f.Op, f.Value is { } v ? ExportValue(v, f.Field.FieldId, n) : null);
        }

        var def = new AppReportDefinition(
            d.Version, Module(d.MainModuleKey),
            d.Child is { } c ? new AppReportChild(Module(c.ModuleKey), Field(c.LinkFieldId) ?? "") : null,
            (d.Columns ?? []).Select(Ref).OfType<AppReportFieldRef>().ToList(),
            (d.Groups ?? []).Select(g => Ref(g.Field) is { } r ? new AppReportGroup(r, g.Bucket) : null).OfType<AppReportGroup>().ToList(),
            (d.Measures ?? []).Select(m => new AppReportMeasure(m.Id, m.Kind, Ref(m.Field), Filter(m.Condition))).ToList(),
            (d.Filters ?? []).Select(Filter).OfType<AppReportFilter>().ToList(),
            Ref(d.PrimaryDateField), d.DateRange,
            (d.Sort ?? []).Select(s => new AppReportSort(ExportSortKey(s.Key, d, Field), s.Desc)).ToList(), d.Viz);
        return (errors.Count == 0 ? def : null, errors.Distinct().ToList());
    }

    private static string ExportSortKey(string key, ReportDefinition d, Func<long, string?> field)
    {
        if ((d.Measures ?? []).Any(m => m.Id == key)) return key;
        if (ReportFieldKeys.TryParse(key) is not { } r) return key;
        var f = r.FieldId == 0 ? "" : field(r.FieldId) ?? "";
        var via = r.ViaFieldId is { } v ? field(v) ?? "" : "";
        return $"{r.Source}:{f}:{via}";
    }

    private static JsonElement ExportValue(JsonElement value, long fieldId, Names n)
    {
        if (fieldId == 0 || !n.IsOptionField(fieldId)) return value;
        JsonNode? Walk(JsonNode? node) => node switch
        {
            JsonArray array => new JsonArray(array.Select(Walk).ToArray()),
            JsonValue v when v.TryGetValue<string>(out var s) && OptionValue().Match(s) is { Success: true } match
                && long.TryParse(match.Groups[1].Value, out var id) && n.Option(fieldId, id) is { } code => JsonValue.Create($"opt_{code}"),
            _ => node?.DeepClone()
        };
        return JsonSerializer.SerializeToElement(Walk(JsonNode.Parse(value.GetRawText())));
    }
}

/// <summary>限定名 &lt;模組代號&gt;.&lt;代號&gt; 的拆解（TemplateGraph.SplitQualified 的薄包裝，避免報表編碼器依賴整個範本圖）。</summary>
internal static class TemplateQualified
{
    public static string? ModuleOf(string qualified) => TemplateService.TemplateGraph.SplitQualified(qualified)?.Module;
}
