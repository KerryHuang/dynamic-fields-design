using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;

namespace DynamicModules.Domain.FieldTypes.Handlers;

/// <summary>日期時間：存成營運時區的偏移字串（例 <c>2026-11-11T10:00:00+08:00</c>）；偏移由 <paramref name="businessOffset"/> 提供（沒給是出廠 +08:00）。</summary>
public sealed class DateTimeHandler(Func<TimeSpan>? businessOffset = null) : IFieldTypeHandler
{
    private readonly Func<TimeSpan> _offset = businessOffset ?? (() => PolicyCatalog.FactoryBusinessOffset);

    public FieldType Type => FieldType.DateTime;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.DateTimeOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        HandlerHelpers.SingleDate(BusinessClock.ParseWallClock(value.GetString()!, _offset()));

    private static readonly Regex Iso = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant);

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx)
    {
        if (HandlerHelpers.IsNull(input)) return FieldValueResult.Ok(null);
        const string error = FieldErrors.DateTimeFormat;
        if (input!.Value.ValueKind != JsonValueKind.String) return FieldValueResult.Fail(error);
        var s = input.Value.GetString()!;
        if (!Iso.IsMatch(s) || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return FieldValueResult.Fail(error);
        // 換成營運時區後超出 DateTime 範圍（正偏移看 9999 年尾、負偏移看 0001 年初）要回欄位錯誤，不丟例外
        var offset = _offset();
        if (BusinessClock.Overflows(dto, offset)) return FieldValueResult.Fail(error);
        var text = dto.ToOffset(offset).ToString("yyyy-MM-dd\\THH:mm:ss", CultureInfo.InvariantCulture) + PolicyCatalog.FormatUtcOffset(offset);
        return FieldValueResult.Ok(HandlerHelpers.ToElement(text));
    }
}
