using System.Globalization;
using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;

namespace DynamicModules.Application.Dynamic.TaskRuleService;

/// <summary>任務規則的純函式：觸發判斷（邊緣）、到期日、負責人。不碰資料庫，once／everyTime 的差別由 Stage 處理。</summary>
public static class TaskRuleEvaluator
{
    /// <summary>
    /// added：日誌項目是 Added。onCreate：added；onUpdateMatch：added ? 現在符合 : (原本不符合 &amp;&amp; 現在符合)；
    /// onFieldBecomes：新值等於指定值，且（新增或舊值不等於指定值）；manual 不由日誌觸發。
    /// ruleHoldsBefore 是 null（不知道舊狀態）時視為原本不符合。
    /// </summary>
    public static bool Triggers(TaskRuleTrigger trigger, bool added, bool? ruleHoldsBefore, bool ruleHoldsAfter,
        IReadOnlyDictionary<string, JsonElement?>? before, IReadOnlyDictionary<string, JsonElement?> after)
    {
        switch (trigger.On)
        {
            case "onCreate":
                return added;
            case "onUpdateMatch":
                return added ? ruleHoldsAfter : ruleHoldsBefore != true && ruleHoldsAfter;
            case "onFieldBecomes":
                if (trigger.FieldId is not { } id || trigger.Value is not { } value) return false;
                var target = value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : value.GetRawText();
                if (target is null) return false;
                var key = $"f_{id}";
                return Raw(after, key) == target && (added || Raw(before, key) != target);
            default:
                return false;
        }
    }

    /// <summary>
    /// 到期日 = 基準欄位的日期 + 偏移天數。Date 取原值；DateTime 換到 +08:00 取日期（"2027-01-31T17:00:00Z" 是 2027-02-01）。
    /// 沒有基準欄位、沒有值、格式不對或超出日期範圍都回 null。
    /// </summary>
    public static DateOnly? DueDate(TaskRuleTask def, Module module, IReadOnlyDictionary<string, JsonElement?> values)
    {
        if (def.DueFieldId is not { } id) return null;
        var field = module.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == id);
        if (field is null || !values.TryGetValue($"f_{id}", out var v) || v is not { ValueKind: JsonValueKind.String } e) return null;
        var text = e.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;

        DateOnly? baseDate = field.Type switch
        {
            FieldType.Date => text.Length >= 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
            FieldType.DateTime => BusinessClock.TryParseWallClock(text, out var at) ? DateOnly.FromDateTime(at) : null,
            _ => null,
        };
        if (baseDate is not { } date) return null;
        try { return date.AddDays(def.DueOffsetDays); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>負責人欄位的值；使用者不存在或已停用（不在 activeUserIds 裡）回 null。</summary>
    public static string? Assignee(TaskRuleTask def, IReadOnlyDictionary<string, JsonElement?> values, IReadOnlySet<string> activeUserIds)
    {
        if (!values.TryGetValue($"f_{def.AssigneeFieldId}", out var v) || v is not { ValueKind: JsonValueKind.String } e) return null;
        var id = e.GetString();
        return !string.IsNullOrEmpty(id) && activeUserIds.Contains(id) ? id : null;
    }

    /// <summary>欄位的值有沒有變：比較 GetRawText，null 與沒有值一樣（同 EventStage.Raw）。</summary>
    public static bool Changed(long? fieldId, IReadOnlyDictionary<string, JsonElement?>? before, IReadOnlyDictionary<string, JsonElement?> after)
    {
        if (fieldId is not { } id) return false;
        var key = $"f_{id}";
        return Raw(before, key) != Raw(after, key);
    }

    private static string? Raw(IReadOnlyDictionary<string, JsonElement?>? d, string key) =>
        d is not null && d.TryGetValue(key, out var v) && v is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } e ? e.GetRawText() : null;
}
