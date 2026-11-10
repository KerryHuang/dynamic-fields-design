using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

public sealed class DateTimeHandler : IFieldTypeHandler
{
    public FieldType Type => FieldType.DateTime;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.DateTimeOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        HandlerHelpers.SingleDate(DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture).ToOffset(Taipei).DateTime);

    private static readonly Regex Iso = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant);

    private static readonly TimeSpan Taipei = TimeSpan.FromHours(8);

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx)
    {
        if (HandlerHelpers.IsNull(input)) return FieldValueResult.Ok(null);
        const string error = FieldErrors.DateTimeFormat;
        if (input!.Value.ValueKind != JsonValueKind.String) return FieldValueResult.Fail(error);
        var s = input.Value.GetString()!;
        if (!Iso.IsMatch(s) || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return FieldValueResult.Fail(error);
        var text = dto.ToOffset(Taipei).ToString("yyyy-MM-dd\\THH:mm:ss", CultureInfo.InvariantCulture) + "+08:00";
        return FieldValueResult.Ok(HandlerHelpers.ToElement(text));
    }
}
