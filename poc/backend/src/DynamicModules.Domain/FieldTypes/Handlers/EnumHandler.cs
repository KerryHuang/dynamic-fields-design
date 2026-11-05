using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

public sealed class EnumHandler : IFieldTypeHandler
{
    public FieldType Type => FieldType.Enum;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.PickOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        [EnumIndex(0, value.GetString()!)];

    internal const string Error = "選項不存在或已停用";

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx)
    {
        if (HandlerHelpers.IsNull(input)) return FieldValueResult.Ok(null);
        if (input!.Value.ValueKind != JsonValueKind.String) return FieldValueResult.Fail(Error);
        var s = input.Value.GetString()!;
        var previous = ctx.PreviousValue is { ValueKind: JsonValueKind.String } p ? p.GetString() : null;
        return IsAllowed(s, ctx, previous)
            ? FieldValueResult.Ok(HandlerHelpers.ToElement(Canonical(s)))
            : FieldValueResult.Fail(Error);
    }

    /// <summary>TextValue 放 opt_&lt;id&gt;（篩選用），NumberValue 放選項 id（排序 JOIN Sequence 用）。</summary>
    internal static IndexValue EnumIndex(int seq, string value) =>
        new(seq, value, TryParseId(value, out var id) ? id : null, null, null);

    /// <summary>選項啟用中，或等於舊值（保留既有的停用選項）。</summary>
    internal static bool IsAllowed(string value, FieldTypeContext ctx, string? previous) =>
        value == previous || (TryParseId(value, out var id) && ctx.ActiveOptionIds.Contains(id));

    /// <summary>`opt_05` 正規化成 `opt_5`；不是選項格式的舊值（只可能因等於舊值而通過）原樣保留。</summary>
    internal static string Canonical(string value) =>
        TryParseId(value, out var id) ? $"opt_{id}" : value;

    internal static bool TryParseId(string value, out long id)
    {
        id = 0;
        return value.StartsWith("opt_", StringComparison.Ordinal)
               && long.TryParse(value.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }
}
