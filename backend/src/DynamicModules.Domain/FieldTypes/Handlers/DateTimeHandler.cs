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
        HandlerHelpers.SingleDate(BusinessClock.ParseWallClock(value.GetString()!));

    private static readonly Regex Iso = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant);

    private static readonly TimeSpan Taipei = BusinessClock.Offset;

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx)
    {
        if (HandlerHelpers.IsNull(input)) return FieldValueResult.Ok(null);
        const string error = FieldErrors.DateTimeFormat;
        if (input!.Value.ValueKind != JsonValueKind.String) return FieldValueResult.Fail(error);
        var s = input.Value.GetString()!;
        if (!Iso.IsMatch(s) || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return FieldValueResult.Fail(error);
        // 換成台北時間後超出 DateTime 範圍（9999 年尾）要回欄位錯誤；下限方向 +8 小時不會溢位，
        // 換算 UTC 超出 0001 年下限的字串在 TryParse 就會被擋下。
        if (dto.UtcDateTime > DateTime.MaxValue - Taipei) return FieldValueResult.Fail(error);
        var text = dto.ToOffset(Taipei).ToString("yyyy-MM-dd\\THH:mm:ss", CultureInfo.InvariantCulture) + "+08:00";
        return FieldValueResult.Ok(HandlerHelpers.ToElement(text));
    }
}
