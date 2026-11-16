using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

public sealed class IntegerHandler : IFieldTypeHandler
{
    public FieldType Type => FieldType.Integer;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.NumberOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        HandlerHelpers.SingleNumber(value.GetDouble());

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx)
    {
        if (HandlerHelpers.IsNull(input)) return FieldValueResult.Ok(null);
        const string error = FieldErrors.MustBeInteger;
        if (input!.Value.ValueKind != JsonValueKind.Number || !input.Value.TryGetDecimal(out var d)
            || d != decimal.Truncate(d) || (ctx.EnforceWriteLimits ? Math.Abs(d) > HandlerHelpers.MaxExactInteger : d < long.MinValue || d > long.MaxValue))
            return FieldValueResult.Fail(error);
        return FieldValueResult.Ok(HandlerHelpers.ToElement((long)d));
    }
}
