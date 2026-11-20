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
    // ---- 輸出 ----

    private static IEnumerable<ReportFieldRef> FieldColumnsFor(ReportDefinition d, bool drilling)
    {
        if (d.Columns.Count > 0 && (drilling || d.Groups.Count == 0)) return d.Columns;
        if (!drilling) return [];
        return d.Groups.Select(g => g.Field).Concat(d.Measures.Where(m => m.Field is not null).Select(m => m.Field!))
            .DistinctBy(ReportCalculator.KeyOf);
    }

    private string ColumnLabel(Bound b, LocaleContext lc)
    {
        var field = b.Field.LabelI18n.Resolve(lc);
        if (b.Via is not null)
            return b.Target!.Perms is null
                ? messages.Get(lc, ReportText.HiddenField)
                : messages.Get(lc, ReportText.FieldPath, b.Via.LabelI18n.Resolve(lc), field);
        return b.Ref.ViaFieldId is not null ? messages.Get(lc, ReportText.FieldPath, field, messages.Get(lc, ReportText.Title)) : field;
    }

    private static string ValueTypeOf(Field f) => f.Type switch
    {
        FieldType.Integer or FieldType.Decimal or FieldType.Currency => "number",
        FieldType.Percent => "percent",
        FieldType.Date => "date",
        FieldType.DateTime => "dateTime",
        FieldType.Formula or FieldType.Rollup => f.Config.ResultType == "date" ? "date" : "number",
        _ => "text",
    };

    private ReportColumn MeasureColumn(
        ReportMeasure m, IReadOnlyDictionary<string, Bound> bound, Side main, Side? child, FieldDisplayContext display)
    {
        var lc = new LocaleContext(display.Locale, display.LocaleFallbacks ?? [I18nText.FallbackLocale]);
        string FieldLabel(ReportFieldRef r) => ColumnLabel(bound[ReportCalculator.KeyOf(r)], lc);
        var (label, type) = m.Kind switch
        {
            "countMain" => (messages.Get(lc, ReportText.Count, main.Module.NameI18n.Resolve(lc)), "number"),
            "countChild" => (messages.Get(lc, ReportText.Count, (child ?? main).Module.NameI18n.Resolve(lc)), "number"),
            "ratio" => (messages.Get(lc, ReportText.Ratio, m.Condition is null ? "" : FieldLabel(m.Condition.Field)), "ratio"),
            "countIf" => (CountIfLabel(m.Condition, bound, display, FieldLabel), "number"),
            _ => (messages.Get(lc, m.Kind switch { "sum" => ReportText.Sum, "avg" => ReportText.Avg, "median" => ReportText.Median, "min" => ReportText.Min, _ => ReportText.Max }, m.Field is null ? "" : FieldLabel(m.Field)),
                m.Field is not null && m.Kind is "min" or "max" && ValueTypeOf(bound[ReportCalculator.KeyOf(m.Field!)].Field) is "date" or "dateTime" ? "dayNumber"
                : m.Field is not null && ValueTypeOf(bound[ReportCalculator.KeyOf(m.Field)].Field) == "percent" ? "percent" : "number"),
        };
        if (m.Condition is { } cond && m.Kind is "sum" or "avg" or "median")
            label = EqLabel(cond, bound, display) is { } v
                ? messages.Get(lc, ReportText.ConditionalEq, label, FieldLabel(cond.Field), v)
                : messages.Get(lc, ReportText.Conditional, label);
        return new ReportColumn(m.Id, label, "measure", type);
    }

    private string CountIfLabel(ReportFilter? cond, IReadOnlyDictionary<string, Bound> bound, FieldDisplayContext display, Func<ReportFieldRef, string> fieldLabel)
    {
        var lc = new LocaleContext(display.Locale, display.LocaleFallbacks ?? [I18nText.FallbackLocale]);
        if (cond is null) return messages.Get(lc, ReportText.CountIf, "");
        return EqLabel(cond, bound, display) is { } v
            ? messages.Get(lc, ReportText.CountIfEq, fieldLabel(cond.Field), v)
            : messages.Get(lc, ReportText.CountIf, fieldLabel(cond.Field));
    }

    /// <summary>
    /// 欄名帶出條件值（B4）：只有單一值的 eq 且值轉得成標籤才有；in、gte、多值、使用者與參照欄位等回 null，欄名退回「符合條件」字樣。
    /// </summary>
    private static string? EqLabel(ReportFilter cond, IReadOnlyDictionary<string, Bound> bound, FieldDisplayContext display)
    {
        if (cond.Op != "eq" || cond.Value is not { ValueKind: JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } value) return null;
        var field = bound[ReportCalculator.KeyOf(cond.Field)].Field;
        // 日期時間欄位沒有 eq 運算子，不在清單內；選項已刪除的列舉值轉不出標籤，走後備字樣
        if (field.Type is not (FieldType.Enum or FieldType.Boolean or FieldType.Text or FieldType.Integer or FieldType.Decimal or FieldType.Currency
            or FieldType.Percent or FieldType.Date)) return null;
        if (field.Type == FieldType.Enum && !field.Options.Any(o => $"opt_{o.Id}" == value.GetString())) return null;
        var text = FieldDisplay.Format(field, value, display, null, new Dictionary<string, string>());
        return text.Length == 0 ? null : text;
    }

    private List<ReportGroupResult> LabeledGroups(
        IReadOnlyList<ReportGroupResult> groups, ReportDefinition d, IReadOnlyDictionary<string, Bound> bound, RunContext ctx)
    {
        var fields = d.Groups.Select(g => bound[ReportCalculator.KeyOf(g.Field)]).ToList();
        var sorted = groups.ToList();
        sorted.Sort((x, y) =>
        {
            foreach (var s in d.Sort)
            {
                int c;
                if (d.Measures.Any(m => m.Id == s.Key))
                {
                    c = CompareNullable(x.Values.GetValueOrDefault(s.Key), y.Values.GetValueOrDefault(s.Key), s.Desc);
                }
                else
                {
                    var i = fields.FindIndex(f => f.Key == s.Key);
                    if (i < 0) continue;
                    c = CompareKeys(fields[i], x.GroupKeys[i], y.GroupKeys[i], s.Desc);
                }
                if (c != 0) return c;
            }
            for (var i = 0; i < fields.Count; i++)
            {
                var c = CompareKeys(fields[i], x.GroupKeys[i], y.GroupKeys[i], false);
                if (c != 0) return c;
            }
            return 0;
        });
        return sorted.Select(g => new ReportGroupResult(
            g.GroupKeys, g.GroupKeys.Select((k, i) => GroupLabel(fields[i], k, ctx)).ToList(), g.Values)).ToList();
    }

    private static int CompareNullable(decimal? x, decimal? y, bool desc)
    {
        if (x is null || y is null) return (x is null).CompareTo(y is null); // 沒有值的一律排最後
        return desc ? y.Value.CompareTo(x.Value) : x.Value.CompareTo(y.Value);
    }

    /// <summary>選項依 Sequence、數字依大小，其餘依字串；空白與已刪除、無權限固定排最後，不受方向影響。</summary>
    private static int CompareKeys(Bound b, string x, string y, bool desc)
    {
        var rank = KeyRank(b, x).CompareTo(KeyRank(b, y));
        if (rank != 0) return rank;
        var c = 0;
        if (b.Field.Type is FieldType.Enum or FieldType.MultiEnum)
            c = OptionSequence(b.Field, x).CompareTo(OptionSequence(b.Field, y));
        else if (decimal.TryParse(x, NumberStyles.Number, CultureInfo.InvariantCulture, out var nx)
                 && decimal.TryParse(y, NumberStyles.Number, CultureInfo.InvariantCulture, out var ny)
                 && ValueTypeOf(b.Field) is "number" or "percent")
            c = nx.CompareTo(ny);
        if (c == 0) c = string.CompareOrdinal(x, y);
        return desc ? -c : c;
    }

    private static int KeyRank(Bound b, string key)
    {
        if (key.Length == 0) return 1;
        if (!b.MayBeUnavailable) return 0;
        return key == ReportCalculator.MissingKey ? 2 : key == ReportCalculator.RestrictedKey ? 3 : 0;
    }

    private static int OptionSequence(Field f, string key) =>
        f.Options.FirstOrDefault(o => $"opt_{o.Id}" == key)?.Sequence ?? int.MaxValue;

    private string GroupLabel(Bound b, string key, RunContext ctx)
    {
        if (key.Length == 0) return ctx.BlankLabels.GetValueOrDefault(b.Key) ?? messages.Get(ctx.Lc, ReportText.Blank);
        if (b.MayBeUnavailable && key == ReportCalculator.MissingKey) return messages.Get(ctx.Lc, ReportText.Deleted);
        if (b.MayBeUnavailable && key == ReportCalculator.RestrictedKey) return messages.Get(ctx.Lc, ReportText.NoPermission);
        return ValueLabel(b, key, ctx);
    }

    /// <summary>Text 裡的鍵換成顯示文字：選項標籤、是否、使用者姓名、參照標題；其他型別鍵就是文字本身。</summary>
    private static string ValueLabel(Bound b, string key, RunContext ctx)
    {
        JsonElement? raw = b.Field.Type switch
        {
            FieldType.Enum or FieldType.User => JsonSerializer.SerializeToElement(key),
            FieldType.MultiEnum => JsonSerializer.SerializeToElement(new[] { key }),
            FieldType.Boolean => JsonSerializer.SerializeToElement(key == "true"),
            _ => null,
        };
        if (raw is not null) return FieldDisplay.Format(b.Field, raw, ctx.Display, null, ctx.UserNames);
        return b.Field.Type == FieldType.Reference ? ctx.Titles.GetValueOrDefault((b.Key, key), key) : key;
    }

    /// <summary>明細的顯示值與排序後的列（同順序；任務來源用列換出 TaskIds）。</summary>
    private (List<IReadOnlyList<object?>> Values, List<ReportRow> Ordered) DetailRows(
        IReadOnlyList<ReportRow> rows, IReadOnlyList<Bound> columns, ReportDefinition d, IReadOnlyDictionary<string, Bound> bound, RunContext ctx)
    {
        var sorts = d.Sort.Where(s => bound.ContainsKey(s.Key) && d.Measures.All(m => m.Id != s.Key)).ToList();
        IEnumerable<ReportRow> ordered = rows;
        if (sorts.Count > 0)
            ordered = rows.OrderBy(r => r, Comparer<ReportRow>.Create((x, y) =>
            {
                foreach (var s in sorts)
                {
                    var c = CompareCells(bound[s.Key], x.Cells[s.Key], y.Cells[s.Key], s.Desc);
                    if (c != 0) return c;
                }
                return 0;
            }));
        var list = ordered.ToList();
        return (list.Select(r => (IReadOnlyList<object?>)columns.Select(b => Display(b, r.Cells[b.Key], ctx)).ToList()).ToList(), list);
    }

    private static int CompareCells(Bound b, ReportCell x, ReportCell y, bool desc)
    {
        if (x.State != Ok || y.State != Ok) return (x.State != Ok).CompareTo(y.State != Ok);
        if (x.Number is { } nx && y.Number is { } ny) return desc ? ny.CompareTo(nx) : nx.CompareTo(ny);
        if (x.Date is { } dx && y.Date is { } dy)
        {
            var c = (x is TimedCell tx && y is TimedCell ty) ? tx.At.CompareTo(ty.At) : dx.CompareTo(dy);
            return desc ? -c : c;
        }
        return CompareKeys(b, x.Text ?? x.Multi?[0] ?? "", y.Text ?? y.Multi?[0] ?? "", desc);
    }

    /// <summary>明細一格：數字回 decimal，其餘回顯示文字，沒有值回 null。</summary>
    private object? Display(Bound b, ReportCell c, RunContext ctx)
    {
        switch (c.State)
        {
            case Restricted: return messages.Get(ctx.Lc, ReportText.NoPermission);
            case Missing: return messages.Get(ctx.Lc, ReportText.Deleted);
            case Empty: return ctx.BlankLabels.GetValueOrDefault(b.Key);
        }
        if (c.Number is { } n) return n;
        if (c is TimedCell t) return t.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        if (c.Date is { } d) return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (c.Multi is { } multi) return FieldDisplay.Format(b.Field, JsonSerializer.SerializeToElement(multi), ctx.Display, null, ctx.UserNames);
        return ValueLabel(b, c.Text!, ctx);
    }
}
