using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

public sealed class UrlHandler : IFieldTypeHandler
{
    public FieldType Type => FieldType.Url;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.TextOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        HandlerHelpers.SingleText(value);

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx)
    {
        if (HandlerHelpers.IsNull(input)) return FieldValueResult.Ok(null);
        const string error = FieldErrors.UrlFormat;
        if (input!.Value.ValueKind != JsonValueKind.String) return FieldValueResult.Fail(error);
        var s = input.Value.GetString()!;
        var prefixed = s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                       || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        return prefixed && Uri.TryCreate(s, UriKind.Absolute, out _)
            ? FieldValueResult.Ok(HandlerHelpers.ToElement(s))
            : FieldValueResult.Fail(error);
    }
}
