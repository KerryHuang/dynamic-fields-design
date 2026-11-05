using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

public sealed class EmailHandler : IFieldTypeHandler
{
    public FieldType Type => FieldType.Email;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.TextOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        HandlerHelpers.SingleText(value);

    private static readonly Regex Pattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+\z", RegexOptions.CultureInvariant);

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx) =>
        HandlerHelpers.NormalizePattern(input, Pattern, "Email 格式錯誤");
}
