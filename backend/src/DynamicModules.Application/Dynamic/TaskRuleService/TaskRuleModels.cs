using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Rules;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.TaskRuleService;

public sealed record TaskRuleTriggerInput(string On, long? FieldId, JsonElement? Value);

/// <summary>Title、Description 是多語系文字；產生任務時依觸發者語系解析成單一字串（設計 §5）。</summary>
public sealed record TaskRuleTaskInput(
    string Code, I18nText Title, I18nText? Description, long AssigneeFieldId, long? DueFieldId, int DueOffsetDays, JsonElement? When);

public sealed record TaskRuleInput(
    string Key, I18nText Name, bool Active, TaskRuleRepeat Repeat, TaskRuleTriggerInput Trigger, JsonElement? When, List<TaskRuleTaskInput> Tasks);

/// <summary>Warnings 在讀取時即時算出，例如規則引用的欄位已被刪除。</summary>
public sealed record TaskRuleResponse(
    string Key, I18nText Name, string ModuleKey, bool Active, string? AppKey, TaskRuleRepeat Repeat,
    TaskRuleTriggerInput Trigger, JsonElement? When, List<TaskRuleTaskInput> Tasks, IReadOnlyList<string> Warnings);

public static class TaskRuleMessages
{
    public const string RuleNotFound = "找不到任務規則";
    public const string ModuleNotFound = "找不到模組";
    public const string KeyExists = "規則代碼已存在";
    public const string KeyImmutable = "規則代碼不能更改";
    public const string BadKey = "規則代碼只能用小寫英文、數字、連字號，2 到 40 字，英文開頭";
    public const string DetailModule = "明細模組不能設定任務規則";
    public const string NameInvalid = "名稱必須有繁體中文";
    public const string NameLength = "名稱每個語系必須是 1 到 200 字";
    public const string BadTriggerOn = "觸發種類必須是 onCreate、onUpdateMatch、onFieldBecomes 或 manual";
    public const string UpdateMatchNeedsWhen = "記錄變成符合條件時觸發，條件必填";
    public const string TriggerFieldKind = "指定值觸發只支援下拉或是非欄位";
    public const string TriggerValueInvalid = "指定的值必須是該欄位的選項（下拉）或 true／false（是非）";
    public const string CreateCannotRepeat = "建立時觸發只有一次，不能選每次符合都觸發";
    public static string TasksCount(int max) => $"任務至少 1 個、至多 {max} 個";
    /// <summary>套件結構層只檢查至少 1 個；上限由政策在匯入與開啟時檢查。</summary>
    public const string TasksRequired = "任務至少 1 個";
    public const string BadTaskCode = "任務代碼只能用英文字母、數字、底線，英文字母開頭，至多 40 字";
    public const string DuplicateTaskCode = "任務代碼在同一條規則內不能重複";
    public const string AssigneeField = "負責人必須是這個模組的人員欄位";
    public const string DueField = "到期基準必須是這個模組的日期欄位";
    public const string DueOffsetRange = "到期日偏移必須在 -3650 到 3650 天之間";
    public static string TooManyRules(int max) => $"每個模組至多 {max} 條任務規則";
    public const string FieldDeleted = "引用的欄位已刪除";
}

public static class TaskRuleMapping
{
    public static JsonElement? ParseWhen(string? whenJson)
    {
        if (string.IsNullOrWhiteSpace(whenJson)) return null;
        using var doc = JsonDocument.Parse(whenJson);
        return doc.RootElement.Clone();
    }

    /// <summary>條件為空或 null 存成 null，其他存原文。</summary>
    public static string? WhenText(JsonElement? when) =>
        when is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } w ? w.GetRawText() : null;

    /// <param name="moduleFieldIds">模組目前所有欄位的 Id（含停用的）；規則引用的欄位不在其中就加警告。</param>
    public static TaskRuleResponse ToResponse(this TaskRule r, string moduleKey, string? appKey, IReadOnlySet<long> moduleFieldIds)
    {
        var warnings = new List<string>();
        if (!TaskRuleReferences.FieldIdsOf(r).IsSubsetOf(moduleFieldIds)) warnings.Add(TaskRuleMessages.FieldDeleted);
        var tasks = r.Tasks.OrderBy(t => t.SortOrder).ThenBy(t => t.Id)
            .Select(t => new TaskRuleTaskInput(t.Code, t.TitleI18n, t.DescriptionI18n, t.AssigneeFieldId, t.DueFieldId, t.DueOffsetDays,
                ParseWhen(t.WhenJson)))
            .ToList();
        return new TaskRuleResponse(r.Key, r.NameI18n, moduleKey, r.Active, appKey, r.Repeat,
            new TaskRuleTriggerInput(r.Trigger.On, r.Trigger.FieldId, r.Trigger.Value), ParseWhen(r.WhenJson), tasks, warnings);
    }
}
