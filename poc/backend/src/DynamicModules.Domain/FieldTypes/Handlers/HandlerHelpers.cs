using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicModules.Domain.FieldTypes.Handlers;

internal static class HandlerHelpers
{
    public static bool IsNull(JsonElement? input) =>
        input is null || input.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value);

    public static FieldValueResult NormalizeText(JsonElement? input, int maxLength)
    {
        if (IsNull(input)) return FieldValueResult.Ok(null);
        if (input!.Value.ValueKind != JsonValueKind.String) return FieldValueResult.Fail("必須是文字");
        var text = input.Value.GetString()!.Trim();
        if (text.Length == 0) return FieldValueResult.Ok(null);
        return text.Length > maxLength
            ? FieldValueResult.Fail($"超過 {maxLength} 個字元")
            : FieldValueResult.Ok(ToElement(text));
    }

    public static FieldValueResult NormalizeDecimal(JsonElement? input, int scale, bool enforceWriteLimits = true)
    {
        if (IsNull(input)) return FieldValueResult.Ok(null);
        if (input!.Value.ValueKind != JsonValueKind.Number || !input.Value.TryGetDecimal(out var d))
            return FieldValueResult.Fail("必須是數字");
        d /= 1.0000000000000000000000000000m; // 去除尾端的 0
        var bits = decimal.GetBits(d);
        var actualScale = (bits[3] >> 16) & 0xFF;
        if (actualScale > scale) return FieldValueResult.Fail($"小數最多 {scale} 位");
        var mantissa = ((UInt128)(uint)bits[2] << 64) | ((UInt128)(uint)bits[1] << 32) | (uint)bits[0];
        // 尾端的零不是有效數字（1e15 只有一位）；只改計數，存的值與小數位數不變
        while (mantissa != 0 && mantissa % 10 == 0) mantissa /= 10;
        return enforceWriteLimits && mantissa.ToString().Length > MaxSignificantDigits
            ? FieldValueResult.Fail($"有效數字最多 {MaxSignificantDigits} 位")
            : FieldValueResult.Ok(ToElement(d));
    }

    /// <summary>
    /// 篩選與排序用的索引欄是 double，只有 15 位有效數字內才與 decimal 的記錄值一一對應（超過會讓不同的金額變成同一個索引值）；
    /// 所以記錄寫入時就擋在這個範圍內（<see cref="FieldTypeContext.EnforceWriteLimits"/>；報表與篩選的條件值不套用）。
    /// </summary>
    public const int MaxSignificantDigits = 15;

    /// <summary>整數的上限：2^53 − 1，double 能精確表示的最大連續整數。</summary>
    public const long MaxExactInteger = 9_007_199_254_740_991;

    /// <summary>字串欄位以正則驗證；不符回 error。</summary>
    public static FieldValueResult NormalizePattern(JsonElement? input, Regex pattern, string error)
    {
        if (IsNull(input)) return FieldValueResult.Ok(null);
        if (input!.Value.ValueKind != JsonValueKind.String) return FieldValueResult.Fail(error);
        var s = input.Value.GetString()!;
        return pattern.IsMatch(s) ? FieldValueResult.Ok(ToElement(s)) : FieldValueResult.Fail(error);
    }

    public const int MaxIndexTextLength = 440;

    private static readonly FilterOperator[] Empties = [FilterOperator.IsEmpty, FilterOperator.IsNotEmpty];

    public static readonly IReadOnlyList<FilterOperator> TextOperators =
    [
        FilterOperator.Eq, FilterOperator.Ne, FilterOperator.In, FilterOperator.NotIn,
        FilterOperator.Contains, FilterOperator.StartsWith, .. Empties
    ];

    public static readonly IReadOnlyList<FilterOperator> NumberOperators =
    [
        FilterOperator.Eq, FilterOperator.Ne, FilterOperator.Gt, FilterOperator.Gte,
        FilterOperator.Lt, FilterOperator.Lte, FilterOperator.Between, .. Empties
    ];

    public static readonly IReadOnlyList<FilterOperator> DateOperators =
    [
        FilterOperator.Eq, FilterOperator.Ne, FilterOperator.Gt, FilterOperator.Gte,
        FilterOperator.Lt, FilterOperator.Lte, FilterOperator.Between,
        FilterOperator.WithinNextDays, FilterOperator.WithinLastDays, .. Empties
    ];

    public static readonly IReadOnlyList<FilterOperator> DateTimeOperators =
    [
        FilterOperator.Gt, FilterOperator.Gte, FilterOperator.Lt, FilterOperator.Lte, FilterOperator.Between,
        FilterOperator.WithinNextDays, FilterOperator.WithinLastDays, .. Empties
    ];

    public static readonly IReadOnlyList<FilterOperator> BooleanOperators = [FilterOperator.Eq, .. Empties];

    public static readonly IReadOnlyList<FilterOperator> PickOperators =
    [
        FilterOperator.Eq, FilterOperator.Ne, FilterOperator.In, FilterOperator.NotIn, .. Empties
    ];

    public static readonly IReadOnlyList<FilterOperator> MultiPickOperators =
        [FilterOperator.In, FilterOperator.NotIn, .. Empties];

    public static readonly IReadOnlyList<FilterOperator> NoOperators = [];
    public static readonly IReadOnlyList<IndexValue> NoIndex = [];

    /// <summary>超過 440 字元截取前段，不切斷 surrogate pair。</summary>
    public static string TruncateIndexText(string s)
    {
        if (s.Length <= MaxIndexTextLength) return s;
        var cut = char.IsHighSurrogate(s[MaxIndexTextLength - 1]) ? MaxIndexTextLength - 1 : MaxIndexTextLength;
        return s[..cut];
    }

    public static IReadOnlyList<IndexValue> SingleText(JsonElement value) =>
        [new(0, TruncateIndexText(value.GetString()!), null, null, null)];

    public static IReadOnlyList<IndexValue> SingleNumber(double number) => [new(0, null, number, null, null)];

    public static IReadOnlyList<IndexValue> SingleDate(DateTime date) => [new(0, null, null, date, null)];
}
