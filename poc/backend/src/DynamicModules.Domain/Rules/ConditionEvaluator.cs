using System.Text.Json;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;

namespace DynamicModules.Domain.Rules;

/// <summary>
/// 對記錄值求值已解析的條件（語意見 poc/shared/rule-cases.json，前端有同樣的實作）。
/// 左邊欄位沒有值時只有 isEmpty、ne、notIn 成立；fieldRef 的對方沒有值時只有 ne 成立。
/// </summary>
public static class ConditionEvaluator
{
    /// <param name="node">null（空群組）一律成立。</param>
    /// <param name="values">鍵是 Field.Key，值是已正規化的記錄值。</param>
    public static bool Evaluate(
        ConditionNode? node, IReadOnlyDictionary<string, JsonElement?> values, FieldTypeRegistry registry, DateOnly today) =>
        node switch
        {
            null => true,
            ConditionGroup g when g.All => g.Children.All(c => Evaluate(c, values, registry, today)),
            ConditionGroup g => g.Children.Any(c => Evaluate(c, values, registry, today)),
            FieldCondition c => EvaluateCondition(c, values, registry, today),
            _ => throw new InvalidOperationException($"未知的條件節點 {node.GetType().Name}")
        };

    private static bool EvaluateCondition(
        FieldCondition c, IReadOnlyDictionary<string, JsonElement?> values, FieldTypeRegistry registry, DateOnly today)
    {
        var left = IndexOf(c.Field, values, registry);
        if (c.Op == FilterOperator.IsEmpty) return left.Count == 0;
        if (c.Op == FilterOperator.IsNotEmpty) return left.Count > 0;
        if (left.Count == 0) return c.Op is FilterOperator.Ne or FilterOperator.NotIn;

        var operands = c.Values;
        if (c.RefField is { } refField)
        {
            var right = IndexOf(refField, values, registry);
            if (right.Count == 0) return c.Op == FilterOperator.Ne;
            operands = [right[0]];
        }

        return c.Op switch
        {
            FilterOperator.Eq => Compare(left[0], operands[0]) == 0,
            FilterOperator.Ne => Compare(left[0], operands[0]) != 0,
            FilterOperator.In => left.Any(l => operands.Any(o => Compare(l, o) == 0)),
            FilterOperator.NotIn => !left.Any(l => operands.Any(o => Compare(l, o) == 0)),
            FilterOperator.Gt => CompareOrNull(left[0], operands[0]) is > 0,
            FilterOperator.Gte => CompareOrNull(left[0], operands[0]) is >= 0,
            FilterOperator.Lt => CompareOrNull(left[0], operands[0]) is < 0,
            FilterOperator.Lte => CompareOrNull(left[0], operands[0]) is <= 0,
            FilterOperator.Between => CompareOrNull(left[0], operands[0]) is >= 0 && CompareOrNull(left[0], operands[1]) is <= 0,
            FilterOperator.Contains => left[0].TextValue!.Contains(operands[0].TextValue!, StringComparison.Ordinal),
            FilterOperator.StartsWith => left[0].TextValue!.StartsWith(operands[0].TextValue!, StringComparison.Ordinal),
            FilterOperator.WithinNextDays => WithinDays(left[0], today, today.AddDays(c.Days!.Value)),
            FilterOperator.WithinLastDays => WithinDays(left[0], today.AddDays(-c.Days!.Value), today),
            FilterOperator.BeforeToday => left[0].DateValue is { } d && DateOnly.FromDateTime(d) < today,
            _ => throw new InvalidOperationException($"規則條件不支援運算子 {c.Op}")
        };
    }

    /// <summary>沒有值（缺少、null、空陣列）回空清單；textarea 不建索引，只看是否為空字串。</summary>
    private static IReadOnlyList<IndexValue> IndexOf(
        Field field, IReadOnlyDictionary<string, JsonElement?> values, FieldTypeRegistry registry)
    {
        if (!values.TryGetValue(field.Key, out var v) || v is not { } el
            || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];
        if (field.Type == FieldType.Textarea)
            return el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s
                ? [new IndexValue(0, s, null, null, null)]
                : [];
        return registry.Get(field.Type).ToIndex(el, field);
    }

    /// <summary>數字、日期、文字（ordinal）、RefKey 依序比對；兩邊種類不同視為不相等且不可比。</summary>
    private static int? CompareOrNull(IndexValue a, IndexValue b)
    {
        if (a.NumberValue is { } an && b.NumberValue is { } bn) return an.CompareTo(bn);
        if (a.DateValue is { } ad && b.DateValue is { } bd) return ad.CompareTo(bd);
        if (a.TextValue is { } at && b.TextValue is { } bt) return Math.Sign(string.CompareOrdinal(at, bt));
        if (a.RefKey is { } ar && b.RefKey is { } br) return Math.Sign(string.CompareOrdinal(ar, br));
        return null;
    }

    // 不可比時回 2，讓 Eq 不成立、Ne 成立；大小比較（gt/gte/lt/lte/between）直接用 CompareOrNull，不可比一律不成立
    private static int Compare(IndexValue a, IndexValue b) => CompareOrNull(a, b) ?? 2;

    private static bool WithinDays(IndexValue v, DateOnly from, DateOnly to)
    {
        if (v.DateValue is not { } d) return false;
        var day = DateOnly.FromDateTime(d);
        return day >= from && day <= to;
    }
}
