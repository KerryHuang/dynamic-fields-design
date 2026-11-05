using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

public sealed class BooleanHandler : IFieldTypeHandler
{
    public FieldType Type => FieldType.Boolean;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.BooleanOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        HandlerHelpers.SingleNumber(value.GetBoolean() ? 1 : 0);

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx)
    {
        if (HandlerHelpers.IsNull(input)) return FieldValueResult.Ok(null);
        return input!.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? FieldValueResult.Ok(HandlerHelpers.ToElement(input.Value.GetBoolean()))
            : FieldValueResult.Fail("必須是是或否");
    }
}
