using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

public sealed class PhoneHandler : IFieldTypeHandler
{
    public FieldType Type => FieldType.Phone;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.TextOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        HandlerHelpers.SingleText(value);

    private static readonly Regex Pattern = new(@"^[0-9 +\-()#]{1,30}\z", RegexOptions.CultureInvariant);

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx) =>
        HandlerHelpers.NormalizePattern(input, Pattern, FieldErrors.PhoneFormat);
}
