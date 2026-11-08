using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.RecordService;

/// <summary>
/// 索引的精確整數值（設計 §5.2 第 3 點）：十進位值 × 10^Scale，全程用 decimal 運算，不經 double。
/// 跨記錄驗證的 SUM 用它，所以加總不會有浮點誤差；放大後超出 long 範圍就回 null（呼叫端決定要 400 還是略過）。
/// </summary>
public static class ScaledNumber
{
    private static readonly FieldType[] Numeric = [FieldType.Integer, FieldType.Decimal, FieldType.Currency, FieldType.Percent, FieldType.Formula, FieldType.Rollup];

    public static bool Applies(Field field) => Numeric.Contains(field.Type) && field.Config.ResultType != "date";

    public static int ScaleOf(Field field) => ScaleOf(field.Type, field.Config);

    public static int ScaleOf(FieldType type, DynamicModules.Domain.ValueObjects.FieldConfig config) =>
        type == FieldType.Integer ? 0 : Math.Clamp(config.Scale ?? 2, 0, 6);

    /// <summary>
    /// 索引用的精確換算：原值 × 10^Scale 不是整數（小數位數比目前 Scale 多，例如 Scale 被調低後的舊值）或超出 long 就回 null，
    /// 不像 <see cref="Of"/> 四捨五入。報表的 SQL 聚合只信非 null 的 ScaledValue，所以非 null 即等於記錄值 × 10^Scale。
    /// </summary>
    public static long? OfExact(Field field, JsonElement value)
    {
        if (!Applies(field) || value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var d)) return null;
        try
        {
            var scaled = d * Pow10(ScaleOf(field));
            return scaled == decimal.Truncate(scaled) && scaled is >= long.MinValue and <= long.MaxValue ? (long)scaled : null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    public static long? Of(Field field, JsonElement value)
    {
        if (!Applies(field) || value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var d)) return null;
        try
        {
            var scaled = Math.Round(d * Pow10(ScaleOf(field)), 0, MidpointRounding.AwayFromZero);
            return scaled is >= long.MinValue and <= long.MaxValue ? (long)scaled : null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static decimal Pow10(int n)
    {
        var r = 1m;
        for (var i = 0; i < n; i++) r *= 10m;
        return r;
    }
}
