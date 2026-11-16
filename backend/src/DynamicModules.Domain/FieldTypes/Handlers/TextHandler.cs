using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

public sealed class TextHandler : IFieldTypeHandler
{
    public FieldType Type => FieldType.Text;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.TextOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        HandlerHelpers.SingleText(value);

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx) =>
        HandlerHelpers.NormalizeText(input, field.Config.MaxLength ?? 255);
}
