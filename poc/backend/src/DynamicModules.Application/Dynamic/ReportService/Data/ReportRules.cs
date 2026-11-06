using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ReportService.Data;

/// <summary>報表定義的存檔／預覽驗證：結構（不碰資料庫）與依模組 metadata、觀看者權限的檢查。runner 假設定義已通過這裡，否則會丟例外。</summary>
public static class ReportRules
{
    public const string ReportNotFound = "報表不存在";
    public const int MaxNameLength = 100;
    public const int DrillLimit = 500;

    private const int MaxDays = 3650, MaxListItems = 50, MaxTextLength = 440;

    private static readonly string[] Vizs = ["table", "indicator", "line", "bar", "donut"];
    private static readonly string[] Buckets = ["day", "week", "month", "quarter", "year"];
    private static readonly string[] Kinds = ["sum", "avg", "min", "max", "countMain", "countChild", "ratio"];
    private static readonly string[] RangeKinds =
        ["today", "thisWeek", "lastWeek", "thisMonth", "lastMonth", "thisYear", "lastYear", "lastDays", "lastWeeks", "lastMonths"];
    private static readonly string[] LastKinds = ["lastDays", "lastWeeks", "lastMonths"];

    private static readonly Dictionary<string, FilterOperator> Operators =
        Enum.GetValues<FilterOperator>().ToDictionary(o => JsonNamingPolicy.CamelCase.ConvertName(o.ToString()));

    private static readonly Regex NoOffsetDateTime = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?$", RegexOptions.CultureInvariant);

    public static Dictionary<string, string[]> ToErrors(string key, IReadOnlyList<string> messages) =>
        new() { [key] = messages.ToArray() };

    public static string? CheckName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "名稱必填" : name.Trim().Length > MaxNameLength ? $"名稱最多 {MaxNameLength} 個字" : null;

    /// <summary>日期區間：種類合法，N 給了就必須 1 到 3650。覆寫用的區間（run、drill）也走這裡。</summary>
    public static string? CheckRange(ReportDateRange? r)
    {
        if (r is null) return null;
        if (r.Kind is null || !RangeKinds.Contains(r.Kind)) return "日期區間種類不合法";
        if (r.N is { } n && (n < 1 || n > MaxDays)) return $"日期區間的 N 必須介於 1 到 {MaxDays}";
        if (r.N is null && LastKinds.Contains(r.Kind)) return "日期區間需要 N";
        return null;
    }

    /// <summary>結構檢查：版本、清單與引用不可缺、量值 Id 唯一、量值與分組的組合合法。通過後才能載入模組做後續檢查。</summary>
    public static List<string> CheckShape(ReportDefinition d)
    {
        var errors = new List<string>();
        if (d.Version != ReportDefinition.CurrentVersion) errors.Add($"定義版本必須是 {ReportDefinition.CurrentVersion}");
        if (string.IsNullOrWhiteSpace(d.MainModuleKey)) errors.Add("主模組必填");
        if (d.Viz is null || !Vizs.Contains(d.Viz)) errors.Add("圖表種類不合法");
        if (d.Columns is null || d.Groups is null || d.Measures is null || d.Filters is null || d.Sort is null)
        {
            errors.Add("欄位、分組、量值、條件、排序清單都必須提供");
            return errors;
        }
        if (d.Columns.Any(c => c is null) || d.Groups.Any(g => g?.Field is null) || d.Measures.Any(m => m is null)
            || d.Filters.Any(f => f?.Field is null || f.Op is null) || d.Sort.Any(s => s?.Key is null)
            || d.Measures.Any(m => m.Id is null || m.Kind is null || (m.Condition is not null && (m.Condition.Field is null || m.Condition.Op is null))))
        {
            errors.Add("清單內容不可有空項目");
            return errors;
        }
        if (d.Child is not null && string.IsNullOrWhiteSpace(d.Child.ModuleKey)) errors.Add("子模組必填");
        if (ReportRunner.AllRefs(d).Concat(d.PrimaryDateField is null ? [] : [d.PrimaryDateField])
            .Any(r => r.Source is not ("main" or "child") || (r.Source == "child" && d.Child is null)))
            errors.Add("欄位來源必須是主模組，或已合併子模組時的子模組");
        if (d.Columns.Count == 0 && d.Measures.Count == 0) errors.Add("至少要選一個欄位或量值");

        if (d.Measures.Select(m => m.Id).Distinct().Count() != d.Measures.Count || d.Measures.Any(m => m.Id.Length == 0))
            errors.Add("量值 Id 不可空白或重複");
        foreach (var m in d.Measures)
        {
            if (!Kinds.Contains(m.Kind)) errors.Add($"量值 {m.Id} 的種類不合法");
            else if (m.Kind is "sum" or "avg" or "min" or "max" && m.Field is null) errors.Add($"量值 {m.Id} 需要欄位");
            else if (m.Kind == "ratio" && m.Condition is null) errors.Add($"量值 {m.Id} 需要條件");
            else if (m.Kind == "countChild" && d.Child is null) errors.Add($"量值 {m.Id} 需要合併子模組");
        }

        foreach (var g in d.Groups)
            if (g.Bucket is not null && !Buckets.Contains(g.Bucket)) errors.Add("分桶只能是 day、week、month、quarter、year");
        // 分桶以欄位為單位記錄；同一欄位只要用過分桶就只能出現一次，否則兩個分組會互相覆蓋
        if (d.Groups.GroupBy(g => ReportCalculator.KeyOf(g.Field)).Any(g => g.Count() > 1 && g.Any(x => x.Bucket is not null)))
            errors.Add("同一欄位不可用兩種分桶分組");

        if (CheckRange(d.DateRange) is { } rangeError) errors.Add(rangeError);
        if (d.DateRange is not null && d.PrimaryDateField is null) errors.Add("設定日期區間需要選主要日期欄位");
        return errors;
    }

    /// <summary>
    /// 依模組 metadata 與觀看者權限檢查，回傳錯誤與條件值正規化後的定義。觀看者看不到的欄位視同不存在。
    /// 運算子與條件值沿用欄位型別 handler（與列表篩選相同），正規化後的值才存檔；不帶偏移的日期時間條件值視為台北牆上時間。
    /// </summary>
    public static (List<string> Errors, ReportDefinition? Normalized) Check(
        ReportDefinition d, ReportModuleSet set, FieldTypeRegistry registry, IReadOnlySet<string> userIds)
    {
        var errors = new List<string>();
        if (set.Main is null) return (["主模組不存在或未啟用"], null);
        if (ModuleRelations.IsDetail(set.Main.Module)) return (["明細模組不能當主模組"], null);
        if (d.Child is not null)
        {
            if (set.Child is null) return (["子模組不存在或未啟用"], null);
            var link = Active(set.Child.Module, d.Child.LinkFieldId);
            if (link is not { Type: FieldType.Reference, Config.RefTarget: { Kind: ReferenceTarget.ModuleKind } target } || target.Key != set.Main.Module.Key
                || !set.Child.Perms.CanSee(link))
                errors.Add("合併欄位必須是子模組指回主模組的參照欄位");
        }

        var excluded = new Dictionary<long, IReadOnlySet<long>>();
        bool IsExcluded(Module m, Field f) =>
            (excluded.TryGetValue(m.Id, out var ids) ? ids : excluded[m.Id] = ReportFieldEligibility.ExcludedFieldIds(m)).Contains(f.Id);

        Field? Resolve(ReportFieldRef r)
        {
            var side = r.Source == "main" ? set.Main : set.Child;
            if (side is null) { errors.Add("欄位來源不存在"); return null; }
            Field? field;
            ReportSide owner = side;
            if (r.ViaFieldId is null) field = Active(side.Module, r.FieldId);
            else
            {
                // 帶出只走一層：Via 必須是來源模組自己的參照欄位
                var via = Active(side.Module, r.ViaFieldId.Value);
                if (via is not { Type: FieldType.Reference, Config.RefTarget: { } target } || !side.Perms.CanSee(via)) { errors.Add($"帶出的參照欄位 {r.ViaFieldId} 不存在或無權限"); return null; }
                if (target.Kind == ReferenceTarget.EntityKind) field = r.FieldId == 0 ? via : null;
                else if (set.Targets.TryGetValue(target.Key, out var t)) { owner = t; field = Active(t.Module, r.FieldId); }
                else { errors.Add("帶出的目標模組不存在或未啟用"); return null; }
            }
            if (field is null || !owner.Perms.CanSee(field)) { errors.Add($"欄位 {r.FieldId} 不存在、未啟用或無權限"); return null; }
            // 帶出只走一層：目標模組的欄位不能再是參照欄位
            if (field is { Type: FieldType.Reference } && owner != side) { errors.Add($"帶出的欄位 {r.FieldId} 不能再是參照欄位"); return null; }
            if (IsExcluded(owner.Module, field)) { errors.Add($"欄位 {r.FieldId} 不能用於報表（彙總欄位或引用彙總的公式）"); return null; }
            return field;
        }

        foreach (var r in ReportRunner.AllRefs(d).DistinctBy(ReportCalculator.KeyOf))
            Resolve(r);

        foreach (var g in d.Groups)
        {
            if (Resolve(g.Field) is not { } f) continue;
            if (!ReportFieldEligibility.CanGroup(f)) errors.Add($"欄位 {g.Field.FieldId} 不能分組");
            if (g.Bucket is not null && !IsDateLike(f)) errors.Add($"欄位 {g.Field.FieldId} 不是日期欄位，不能分桶");
        }
        foreach (var m in d.Measures.Where(m => m.Field is not null))
        {
            if (Resolve(m.Field!) is not { } f) continue;
            if (m.Kind is "sum" or "avg" && !ReportFieldEligibility.CanSum(f)) errors.Add($"量值 {m.Id} 的欄位不能加總或平均");
            if (m.Kind is "min" or "max" && !ReportFieldEligibility.CanMinMax(f)) errors.Add($"量值 {m.Id} 的欄位不能取最小或最大");
        }
        if (d.PrimaryDateField is not null && Resolve(d.PrimaryDateField) is { } primary && !IsDateLike(primary))
            errors.Add("主要日期欄位必須是日期或日期時間");

        ReportFilter? Normalize(ReportFilter f)
        {
            if (Resolve(f.Field) is not { } field) return null;
            var handler = registry.Get(field.Type);
            if (!Operators.TryGetValue(f.Op, out var op) || !handler.OperatorsFor(field).Contains(op))
            {
                errors.Add($"欄位 {f.Field.FieldId} 不支援運算子 {f.Op}");
                return null;
            }
            if (NormalizeValue(field, handler, op, f.Value, registry, userIds) is not { } value)
            {
                errors.Add($"欄位 {f.Field.FieldId} 的條件值格式錯誤");
                return null;
            }
            return f with { Value = value.Value };
        }

        var filters = d.Filters.Select(Normalize).ToList();
        var measures = d.Measures.Select(m => m.Condition is null ? m : m with { Condition = Normalize(m.Condition) ?? m.Condition }).ToList();
        return errors.Count > 0 ? (errors, null) : (errors, d with { Filters = filters.Select(f => f!).ToList(), Measures = measures });
    }

    /// <summary>正規化後的條件值；Value 為 null 表示沒有值（isEmpty 類）。</summary>
    private readonly record struct Norm(JsonElement? Value);

    private static Field? Active(Module m, long id) =>
        m.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == id && f.Status == FieldStatus.Active);

    internal static bool IsDateLike(Field f) =>
        f.Type is FieldType.Date or FieldType.DateTime || (f.Type == FieldType.Formula && f.Config.ResultType == "date");

    /// <summary>回傳 null 代表格式錯誤；有效但無值（isEmpty）回 (null)。結構與 <c>ConditionParser.ParseValue</c> 相同。</summary>
    private static Norm? NormalizeValue(
        Field field, IFieldTypeHandler handler, FilterOperator op, JsonElement? value, FieldTypeRegistry registry, IReadOnlySet<string> userIds)
    {
        if (value is { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }) value = null;
        switch (op)
        {
            case FilterOperator.IsEmpty or FilterOperator.IsNotEmpty:
                return value is null ? new Norm(null) : null;
            case FilterOperator.WithinNextDays or FilterOperator.WithinLastDays:
                return value is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var days) && days is >= 0 and <= MaxDays ? new Norm(value) : null;
            case FilterOperator.Contains or FilterOperator.StartsWith:
                return value is { ValueKind: JsonValueKind.String } s && s.GetString()!.Length is >= 1 and <= MaxTextLength ? new Norm(value) : null;
            case FilterOperator.In or FilterOperator.NotIn:
                if (value is not { ValueKind: JsonValueKind.Array } list || list.GetArrayLength() is < 1 or > MaxListItems) return null;
                var elementHandler = field.Type == FieldType.MultiEnum ? registry.Get(FieldType.Enum) : handler;
                return Many(list.EnumerateArray(), field, elementHandler, registry, userIds);
            case FilterOperator.Between:
                return value is { ValueKind: JsonValueKind.Array } pair && pair.GetArrayLength() == 2
                    ? Many(pair.EnumerateArray(), field, handler, registry, userIds) : null;
            default:
                return value is { } v && One(v, field, handler, registry, userIds) is { } one ? new Norm(one) : null;
        }
    }

    private static Norm? Many(
        IEnumerable<JsonElement> items, Field field, IFieldTypeHandler handler, FieldTypeRegistry registry, IReadOnlySet<string> userIds)
    {
        var result = new List<JsonElement>();
        foreach (var item in items)
        {
            if (One(item, field, handler, registry, userIds) is not { } one) return null;
            result.Add(one);
        }
        return new Norm(JsonSerializer.SerializeToElement(result));
    }

    private static JsonElement? One(JsonElement value, Field field, IFieldTypeHandler handler, FieldTypeRegistry registry, IReadOnlySet<string> userIds)
    {
        if (field.Type == FieldType.DateTime && value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString()!;
            // 只給日期是一整天（runner 的 Span）；沒帶偏移的時間就是台北牆上時間，補上 +08:00 再走 handler
            if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return value;
            if (NoOffsetDateTime.IsMatch(s)) value = JsonSerializer.SerializeToElement(s + "+08:00");
        }
        var normalizer = handler.Type switch
        {
            FieldType.Formula or FieldType.Rollup => registry.Get(field.Config.ResultType == "date" ? FieldType.Date : FieldType.Decimal),
            FieldType.AutoNumber => registry.Get(FieldType.Text),
            _ => handler,
        };
        var ids = field.Options.Select(o => o.Id).ToHashSet();
        var seq = field.Options.ToDictionary(o => o.Id, o => o.Sequence);
        var result = normalizer.Normalize(value, field, new FieldTypeContext(ids, ids, seq, null, userIds));
        return result is { IsValid: true, Value: { } v } ? v : null;
    }
}
