using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Dynamic.Display;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.PrintService;

/// <summary>
/// 列印的欄位值格式：數字照畫面的規則（金額有千分位並依欄位小數位數補零，數量等一般數字不補多餘的零，例如 10 不是 10.00），
/// 其他型別沿用 <see cref="FieldDisplay"/>。匯出與歷程不走這裡，它們要固定小數位數，方便對帳與再匯入。
/// </summary>
internal static class PrintValueFormat
{
    private static readonly decimal TrimZeros = 1.000000000000000000000000000m;

    public static string Format(
        Field field, JsonElement? value, string locale, ReferenceInfo? reference, IReadOnlyDictionary<string, string> userNames)
    {
        if (value is { ValueKind: JsonValueKind.Number } v && NumberText(field, v.GetDecimal()) is { } text) return text;
        return FieldDisplay.Format(field, value, locale, reference, userNames);
    }

    private static string? NumberText(Field field, decimal n) => field.Type switch
    {
        FieldType.Integer or FieldType.Decimal => Plain(n),
        FieldType.Currency => Money(n, field.Config.Scale ?? 2),
        FieldType.Percent => Plain(n) + "%",
        FieldType.Formula or FieldType.Rollup => field.Config.ResultType switch
        {
            "currency" => Money(n, field.Config.Scale ?? 2),
            "date" => null,
            _ => Plain(n),
        },
        _ => null,
    };

    private static string Plain(decimal n) => (n / TrimZeros).ToString(CultureInfo.InvariantCulture);

    private static string Money(decimal n, int scale) => n.ToString("N" + scale, CultureInfo.InvariantCulture);
}
