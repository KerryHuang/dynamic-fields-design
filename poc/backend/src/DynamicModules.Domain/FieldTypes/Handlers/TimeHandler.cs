using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

public sealed class TimeHandler : IFieldTypeHandler
{
    public FieldType Type => FieldType.Time;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.NumberOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        HandlerHelpers.SingleText(value);

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx)
    {
        if (HandlerHelpers.IsNull(input)) return FieldValueResult.Ok(null);
        var s = input!.Value.ValueKind == JsonValueKind.String ? input.Value.GetString()! : "";
        return DateTime.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? FieldValueResult.Ok(HandlerHelpers.ToElement(s))
            : FieldValueResult.Fail(FieldErrors.TimeFormat);
    }
}
