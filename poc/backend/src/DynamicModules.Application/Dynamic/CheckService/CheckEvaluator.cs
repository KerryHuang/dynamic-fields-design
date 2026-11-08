using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Rules;

namespace DynamicModules.Application.Dynamic.CheckService;

/// <summary>一個組：組鍵各欄位的索引值（null 代表這個欄位沒有值）與用來找上限的那筆記錄值。</summary>
public sealed record CheckGroup(string Id, IReadOnlyList<AggregateGroupKey> Keys, IReadOnlyDictionary<string, JsonElement?> Source);

/// <summary>Violation 非 null 是違規訊息（未解析數字的樣板已套用）；Failure 非 null 是整筆要失敗的硬錯誤（索引不完整、溢位）。</summary>
public sealed record GroupOutcome(string? Violation, ServiceResult? Failure, decimal Value, decimal? Limit);

/// <summary>
/// 跨記錄驗證的求值核心（設計 §5.2），寫入後的 CheckStage 與設計器的試跑共用同一份。
/// 狀態是否符合 where 用記憶體內的條件求值；聚合走 SQL（組鍵與 where 同一套參數化轉換）。
/// </summary>
public class CheckEvaluator(IRecordRepository records, IModuleRepository modules, FieldTypeRegistry registry, IBusinessTime time)
{
    public const string NoLimit = "找不到上限";
    public const string Incomplete = "跨記錄驗證的索引不完整，請重新儲存這個檢查以重建索引";
    public const string Overflow = "加總超出可計算範圍";

    public sealed record Prepared(Module Module, ModuleCheck Check, ConditionNode? Where, Dictionary<long, Field> Fields);

    /// <summary>where 用列表篩選的規則解析（欄位已確保可篩選）；解析失敗代表定義壞掉，回傳 failure。</summary>
    public async Task<(Prepared? Prepared, ServiceResult? Failure)> PrepareAsync(Module module, ModuleCheck check, CancellationToken ct)
    {
        ConditionNode? node = null;
        if (check.Definition.Where is { ValueKind: JsonValueKind.Object } where)
        {
            var parsed = ConditionParser.Parse(where, module, registry, ConditionUsage.ListFilter, await modules.ListUserIdsAsync(ct));
            if (parsed.Error is not null) return (null, ServiceResult.Conflict($"檢查「{check.Key}」的條件已不可用：{parsed.Error}"));
            node = parsed.Node;
        }
        var fields = module.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).ToDictionary(f => f.Id);
        return (new Prepared(module, check, node, fields), null);
    }

    /// <summary>這個狀態（寫入前或寫入後的值）符合 where 才有組。</summary>
    public CheckGroup? GroupOf(Prepared p, IReadOnlyDictionary<string, JsonElement?> values)
    {
        if (!ConditionEvaluator.Evaluate(p.Where, values, registry, time.Today)) return null;
        var keys = new List<AggregateGroupKey>();
        var id = new System.Text.StringBuilder();
        foreach (var fid in p.Check.Definition.GroupBy ?? [])
        {
            var field = p.Fields[fid];
            IndexValue? iv = null;
            if (values.TryGetValue(field.Key, out var v) && v is { ValueKind: not JsonValueKind.Null })
                iv = registry.Get(field.Type).ToIndex(v.Value, field).FirstOrDefault();
            keys.Add(new AggregateGroupKey(field, iv));
            // 文字值可以含任何字元，用分隔符串接會讓不同的組鍵串出同一個 Id；用 JSON 陣列編碼才無歧義
            id.Append(JsonSerializer.Serialize(iv is null
                ? new object?[] { fid }
                : [fid, iv.TextValue, iv.NumberValue?.ToString("R", CultureInfo.InvariantCulture), iv.DateValue?.ToString("O", CultureInfo.InvariantCulture), iv.RefKey]));
        }
        return new CheckGroup(id.ToString(), keys, values);
    }

    public async Task<GroupOutcome> EvaluateAsync(Prepared p, CheckGroup group, CancellationToken ct)
    {
        var def = p.Check.Definition;
        var sumField = def.Function == "sum" && def.FieldId is { } sid ? p.Fields[sid] : null;
        AggregateResult agg;
        try
        {
            agg = await records.AggregateAsync(new AggregateQuery(p.Module.Id, group.Keys, p.Where, sumField, time.Today), ct);
        }
        catch (AggregateOverflowException)
        {
            return new GroupOutcome(null, ServiceResult.Conflict(Overflow), 0, null);
        }
        if (agg.MissingScaled > 0) return new GroupOutcome(null, ServiceResult.Conflict(Incomplete), 0, null);

        var value = sumField is null ? agg.Count : agg.ScaledSum / Pow10(ScaledNumber.ScaleOf(sumField));
        var message = def.Message.Resolve(Domain.ValueObjects.I18nText.FallbackLocale);
        var limit = await LimitAsync(def.Assert.Limit, group.Source, ct);
        if (limit is null) return new GroupOutcome($"{NoLimit}（{message}）", null, value, null);
        if (Holds(def.Assert.Op, value, limit.Value)) return new GroupOutcome(null, null, value, limit);
        return new GroupOutcome(message.Replace("{value}", Format(value)).Replace("{limit}", Format(limit.Value)), null, value, limit);
    }

    private async Task<decimal?> LimitAsync(CheckLimit limit, IReadOnlyDictionary<string, JsonElement?> source, CancellationToken ct)
    {
        if (limit.Const is { } c) return c;
        if (limit.Via is not { } via) return limit.Field is { } own ? NumberOf(source, own) : null;
        if (!source.TryGetValue($"f_{via}", out var reference) || reference is not { ValueKind: not JsonValueKind.Null }) return null;
        var idText = reference.Value.ValueKind == JsonValueKind.Number ? reference.Value.GetRawText() : reference.Value.GetString();
        if (!long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
        var target = await records.GetAsync(id, ct);
        return target is null || limit.Field is not { } lf ? null : NumberOf(RecordValueProcessor.Parse(target.ValuesJson), lf);
    }

    private static decimal? NumberOf(IReadOnlyDictionary<string, JsonElement?> values, long fieldId) =>
        values.TryGetValue($"f_{fieldId}", out var v) && v is { ValueKind: JsonValueKind.Number } n && n.TryGetDecimal(out var d) ? d : null;

    private static bool Holds(string op, decimal value, decimal limit) => op switch
    {
        "gte" => value >= limit, "gt" => value > limit, "lte" => value <= limit, "lt" => value < limit, "eq" => value == limit,
        _ => false
    };

    private static string Format(decimal d) => d.ToString("0.############################", CultureInfo.InvariantCulture);

    private static decimal Pow10(int n)
    {
        var r = 1m;
        for (var i = 0; i < n; i++) r *= 10m;
        return r;
    }
}
