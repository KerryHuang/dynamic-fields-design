using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.FieldTypes.Handlers;

/// <summary>參照欄位：模組目標存記錄 Id（number），實體目標存實體主鍵（string）；索引一律用字串 RefKey。</summary>
public sealed class ReferenceHandler : IFieldTypeHandler
{
    private const string NotFound = "參照的資料不存在或無權選取";

    public FieldType Type => FieldType.Reference;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.PickOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        [new(0, null, null, null, KeyOf(value))];

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx)
    {
        if (HandlerHelpers.IsNull(input)) return FieldValueResult.Ok(null);
        var value = input!.Value;
        var expected = field.Config.RefTarget?.Kind == ReferenceTarget.ModuleKind
            ? JsonValueKind.Number
            : JsonValueKind.String;
        if (value.ValueKind != expected) return FieldValueResult.Fail(NotFound);

        var key = KeyOf(value);
        if (key is null || (expected == JsonValueKind.String && key.Length == 0)) return FieldValueResult.Fail(NotFound);
        if (expected == JsonValueKind.Number && !value.TryGetInt64(out _)) return FieldValueResult.Fail(NotFound);

        if (ctx.ValidRefKeys is { } valid && !valid.Contains(key)
            && !(ctx.PreviousValue is { } prev && KeyOf(prev) == key))
            return FieldValueResult.Fail(NotFound);
        return FieldValueResult.Ok(HandlerHelpers.ToElement(value));
    }

    /// <summary>number → Int64 字串、string → 本身，其他 null。</summary>
    public static string? KeyOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetInt64(out var n) ? n.ToString() : null,
        JsonValueKind.String => value.GetString(),
        _ => null
    };
}
