using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.Display;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.FieldTypes.Handlers;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ReportService;
public partial class ReportRunner
{
    /// <summary>只看形狀，不看內容：已停用或刪除的選項、已刪除的使用者仍算對得上，比對時只是不符合。通過後 <see cref="Matches"/> 不會因值的形狀丟例外。</summary>
    private bool ConditionFits(ReportFilter f, Field field)
    {
        if (!Operators.TryGetValue(f.Op, out var op) || !registry.Get(field.Type).OperatorsFor(field).Contains(op)) return false;
        var value = f.Value is { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } ? null : f.Value;
        switch (op)
        {
            case FilterOperator.IsEmpty or FilterOperator.IsNotEmpty:
                return true;
            case FilterOperator.WithinNextDays or FilterOperator.WithinLastDays:
                return value is { ValueKind: JsonValueKind.Number } days && days.TryGetInt32(out _);
            case FilterOperator.Contains or FilterOperator.StartsWith:
                return value is { ValueKind: JsonValueKind.String };
            case FilterOperator.In or FilterOperator.NotIn:
                return value is { ValueKind: JsonValueKind.Array } list && list.EnumerateArray().All(e => ScalarFits(field, e));
            case FilterOperator.Between:
                return value is { ValueKind: JsonValueKind.Array } pair && pair.GetArrayLength() == 2 && pair.EnumerateArray().All(e => ScalarFits(field, e));
            default:
                return value is { } v && ScalarFits(field, v);
        }
    }

    /// <summary>值的形狀要對得上 <see cref="ValueCell"/> 給這個欄位的格子：日期、數字，其餘是可比對的純量。</summary>
    private static bool ScalarFits(Field field, JsonElement v)
    {
        if (ReportRules.IsDateLike(field))
            return v.ValueKind == JsonValueKind.String
                && (DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || TryWallClock(v.GetString()!, out _));
        if (field.Type is FieldType.Integer or FieldType.Decimal or FieldType.Currency or FieldType.Percent or FieldType.Formula or FieldType.Rollup)
            return v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out _);
        return v.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
    }

    // ---- 條件 ----

    /// <summary>
    /// 平鋪 AND 清單裡的一個條件，語意同列表篩選：ne、notIn 包含空值，比較類不含空值。
    /// 看不到或已刪除的目標一律不符合，猜中或猜錯目標的值結果都一樣。
    /// </summary>
    private static bool Matches(ReportFilter f, ReportCell c, DateOnly basis)
    {
        if (c.State is Restricted or Missing) return false;
        var op = Operators[f.Op];
        if (op == FilterOperator.IsEmpty) return c.State == Empty;
        if (op == FilterOperator.IsNotEmpty) return c.State != Empty;
        if (c.State == Empty) return op is FilterOperator.Ne or FilterOperator.NotIn;

        var v = f.Value ?? throw new ArgumentException($"運算子 {f.Op} 需要條件值");
        if (c.Date is { } date) return MatchDate(op, v, c is TimedCell t ? t.At : date.ToDateTime(TimeOnly.MinValue), basis);
        if (c.Number is { } number) return MatchNumber(op, v, number);

        IReadOnlyList<string> texts = c.Multi ?? [c.Text!];
        return op switch
        {
            FilterOperator.Eq => texts.Contains(Scalar(v)),
            FilterOperator.Ne => !texts.Contains(Scalar(v)),
            FilterOperator.In => v.EnumerateArray().Any(e => texts.Contains(Scalar(e))),
            FilterOperator.NotIn => !v.EnumerateArray().Any(e => texts.Contains(Scalar(e))),
            FilterOperator.Contains => texts.Any(x => x.Contains(Scalar(v), StringComparison.OrdinalIgnoreCase)),
            FilterOperator.StartsWith => texts.Any(x => x.StartsWith(Scalar(v), StringComparison.OrdinalIgnoreCase)),
            // 只有時間型別的文字值走到比較類（ConditionFits 已擋其他型別）：HH:mm 字典序即時間序
            FilterOperator.Gt => string.CompareOrdinal(c.Text, Scalar(v)) > 0,
            FilterOperator.Gte => string.CompareOrdinal(c.Text, Scalar(v)) >= 0,
            FilterOperator.Lt => string.CompareOrdinal(c.Text, Scalar(v)) < 0,
            FilterOperator.Lte => string.CompareOrdinal(c.Text, Scalar(v)) <= 0,
            FilterOperator.Between => string.CompareOrdinal(c.Text, Scalar(v[0])) >= 0 && string.CompareOrdinal(c.Text, Scalar(v[1])) <= 0,
            _ => throw new ArgumentException($"文字值不支援運算子 {f.Op}"),
        };
    }

    private static string Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => e.GetRawText(),
    };

    private static bool MatchNumber(FilterOperator op, JsonElement v, decimal n) => op switch
    {
        FilterOperator.Eq => n == v.GetDecimal(),
        FilterOperator.Ne => n != v.GetDecimal(),
        FilterOperator.Gt => n > v.GetDecimal(),
        FilterOperator.Gte => n >= v.GetDecimal(),
        FilterOperator.Lt => n < v.GetDecimal(),
        FilterOperator.Lte => n <= v.GetDecimal(),
        FilterOperator.Between => n >= v[0].GetDecimal() && n <= v[1].GetDecimal(),
        _ => throw new ArgumentException($"數字值不支援運算子 {op}"),
    };

    private static bool MatchDate(FilterOperator op, JsonElement v, DateTime at, DateOnly basis)
    {
        switch (op)
        {
            case FilterOperator.WithinNextDays: return InDays(at, basis, basis.AddDays(v.GetInt32()));
            case FilterOperator.WithinLastDays: return InDays(at, basis.AddDays(-v.GetInt32()), basis);
            case FilterOperator.Between: return at >= Span(v[0]).From && at < Span(v[1]).Until;
        }
        var (from, until) = Span(v);
        return op switch
        {
            FilterOperator.Eq => at >= from && at < until,
            FilterOperator.Ne => at < from || at >= until,
            FilterOperator.Gt => at >= until,
            FilterOperator.Gte => at >= from,
            FilterOperator.Lt => at < from,
            FilterOperator.Lte => at < until,
            _ => throw new ArgumentException($"日期值不支援運算子 {op}"),
        };
    }

    private static bool InDays(DateTime at, DateOnly from, DateOnly toInclusive) =>
        at >= from.ToDateTime(TimeOnly.MinValue) && at < toInclusive.AddDays(1).ToDateTime(TimeOnly.MinValue);

    /// <summary>條件值涵蓋的時間：只給日期是一整天，上界是隔天 00:00 之前（spec §6.4）；有時間就是那一刻。</summary>
    private static (DateTime From, DateTime Until) Span(JsonElement v)
    {
        var s = v.GetString() ?? throw new ArgumentException("日期條件值必須是文字");
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return (d.ToDateTime(TimeOnly.MinValue), d.AddDays(1).ToDateTime(TimeOnly.MinValue));
        if (!TryWallClock(s, out var at)) throw new FormatException($"日期條件值格式錯誤：{s}");
        return (at, at.AddTicks(1));
    }
}
