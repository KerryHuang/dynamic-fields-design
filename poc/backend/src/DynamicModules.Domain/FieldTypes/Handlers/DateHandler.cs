using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

public sealed class DateHandler : IFieldTypeHandler
{
    public FieldType Type => FieldType.Date;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.DateOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        HandlerHelpers.SingleDate(DateTime.ParseExact(value.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture));

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx)
    {
        if (HandlerHelpers.IsNull(input)) return FieldValueResult.Ok(null);
        var s = input!.Value.ValueKind == JsonValueKind.String ? input.Value.GetString()! : "";
        return DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? FieldValueResult.Ok(HandlerHelpers.ToElement(s))
            : FieldValueResult.Fail("日期格式必須是 yyyy-MM-dd");
    }
}
