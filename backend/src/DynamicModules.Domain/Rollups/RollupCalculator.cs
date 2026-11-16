using System.Globalization;
using System.Text.Json;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Rules;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Rollups;

/// <summary>彙總欄位的計算：純函式，不碰資料庫。children 是屬於同一個父記錄、未刪除的子記錄值。</summary>
public static class RollupCalculator
{
    private static readonly FieldTypeRegistry Registry = FieldTypeRegistry.CreateDefault();

    /// <param name="source">子記錄所屬模組；條件與彙總對象依它解析。</param>
    /// <param name="userIds">user 欄位條件的有效使用者 id，由呼叫端取自 IModuleRepository.ListUserIdsAsync。</param>
    /// <returns>sum、count 空集合得 0；min、max 空集合或全部缺值得 null。</returns>
    public static JsonElement? Compute(
        Field rollup, Module source, IEnumerable<IReadOnlyDictionary<string, JsonElement?>> children, DateOnly today,
        IReadOnlySet<string> userIds)
    {
        var cfg = rollup.Config.Rollup ?? throw new InvalidOperationException($"欄位 {rollup.Id} 沒有彙總設定");
        var rows = Filter(cfg, source, children, today, userIds);

        if (cfg.Function == RollupFunction.Count) return Number(rows.Count, 0);

        var aggregate = source.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == cfg.AggregateFieldId)
            ?? throw new InvalidOperationException($"找不到彙總對象欄位 {cfg.AggregateFieldId}");

        if (rollup.Config.ResultType == "date")
        {
            var dates = rows.Select(r => ReadDate(r, aggregate)).Where(d => d is not null).Select(d => d!).ToList();
            if (dates.Count == 0) return null;
            var picked = cfg.Function == RollupFunction.Min ? dates.Min(StringComparer.Ordinal) : dates.Max(StringComparer.Ordinal);
            return JsonSerializer.SerializeToElement(picked);
        }

        var numbers = rows.Select(r => ReadNumber(r, aggregate)).Where(n => n is not null).Select(n => n!.Value).ToList();
        var scale = rollup.Config.Scale ?? 2;
        return cfg.Function switch
        {
            RollupFunction.Sum => Number(numbers.Sum(), scale),
            _ when numbers.Count == 0 => null,
            RollupFunction.Min => Number(numbers.Min(), scale),
            _ => Number(numbers.Max(), scale)
        };
    }

    private static List<IReadOnlyDictionary<string, JsonElement?>> Filter(
        RollupConfig cfg, Module source, IEnumerable<IReadOnlyDictionary<string, JsonElement?>> children, DateOnly today,
        IReadOnlySet<string> userIds)
    {
        if (cfg.Condition is not { ValueKind: JsonValueKind.Object } condition) return children.ToList();
        var parsed = ConditionParser.Parse(condition, source, Registry, ConditionUsage.Rule, userIds);
        // 比照 RuleEngine：設計時已驗證過，執行期解析失敗就略過條件（全部列都算），不能讓存檔失敗
        if (parsed.Error is not null) return children.ToList();
        return children.Where(c => ConditionEvaluator.Evaluate(parsed.Node, c, Registry, today)).ToList();
    }

    private static JsonElement Number(decimal value, int scale) =>
        JsonSerializer.SerializeToElement(Math.Round(value, scale, MidpointRounding.AwayFromZero));

    private static decimal? ReadNumber(IReadOnlyDictionary<string, JsonElement?> row, Field field) =>
        row.TryGetValue(field.Key, out var v) && v is { ValueKind: JsonValueKind.Number } n && n.TryGetDecimal(out var d) ? d : null;

    private static string? ReadDate(IReadOnlyDictionary<string, JsonElement?> row, Field field) =>
        row.TryGetValue(field.Key, out var v) && v is { ValueKind: JsonValueKind.String } s
        && DateOnly.TryParseExact(s.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
}
