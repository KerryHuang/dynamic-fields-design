using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.TaskRuleService;

/// <summary>任務規則定義的驗證（設計 §5）。錯誤鍵是欄位路徑，例如 tasks[0].code、trigger.field。</summary>
public sealed class TaskRuleValidator(IModuleRepository modules, ITaskRuleRepository rules, FieldTypeRegistry registry)
{
    public const int MaxRulesPerModule = 20, MaxTasks = 20, MaxOffsetDays = 3650;
    private static readonly string[] TriggerKinds = ["onCreate", "onUpdateMatch", "onFieldBecomes", "manual"];
    private static readonly Regex TaskCodePattern = new("^[a-z][a-zA-Z0-9_]{0,39}$");

    public static ServiceResult? CheckModule(Module module) =>
        ModuleRelations.IsDetail(module)
            ? ServiceResult.Invalid(new Dictionary<string, string[]> { ["moduleKey"] = [TaskRuleMessages.DetailModule] })
            : null;

    /// <summary>selfKey 是更新中的規則代碼（新增時為 null）；通過時回 null。</summary>
    public async Task<ServiceResult?> ValidateAsync(Module module, TaskRuleInput input, string? selfKey, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        void Err(string key, string message) => errors.TryAdd(key, [message]);

        if (!Regex.IsMatch(input.Key ?? "", ModuleRules.ModuleKeyPattern)) Err("key", TaskRuleMessages.BadKey);
        if (input.Name is null || !input.Name.IsValid(out _)) Err("name", TaskRuleMessages.NameInvalid);
        if (input.Trigger is null) Err("trigger", TaskRuleMessages.BadTriggerOn);
        if (input.Tasks is null || input.Tasks.Count is 0 or > MaxTasks) Err("tasks", TaskRuleMessages.TasksCount);
        if (errors.Count > 0) return ServiceResult.Invalid(errors);

        var fields = module.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);
        var userIds = await modules.ListUserIdsAsync(ct);
        if (CheckWhen(module, input.When, userIds) is { } whenError) Err("when", whenError);
        CheckTrigger(input, fields, Err);

        var codes = new HashSet<string>();
        for (var i = 0; i < input.Tasks!.Count; i++)
        {
            var t = input.Tasks[i];
            var at = $"tasks[{i}]";
            if (!TaskCodePattern.IsMatch(t.Code ?? "")) Err($"{at}.code", TaskRuleMessages.BadTaskCode);
            else if (!codes.Add(t.Code!)) Err($"{at}.code", TaskRuleMessages.DuplicateTaskCode);
            CheckText(t.Title, $"{at}.title", required: true, TaskEditParser.TitleMax, TaskMessages.TitleTooLong, Err);
            CheckText(t.Description, $"{at}.description", required: false, TaskEditParser.DescriptionMax, TaskMessages.DescriptionTooLong, Err);
            if (!fields.TryGetValue(t.AssigneeFieldId, out var assignee) || assignee.Type != FieldType.User || assignee.Status != FieldStatus.Active)
                Err($"{at}.assigneeFieldId", TaskRuleMessages.AssigneeField);
            if (t.DueFieldId is { } dueId
                && (!fields.TryGetValue(dueId, out var due) || due.Type is not (FieldType.Date or FieldType.DateTime) || due.Status != FieldStatus.Active))
                Err($"{at}.dueFieldId", TaskRuleMessages.DueField);
            if (t.DueOffsetDays is < -MaxOffsetDays or > MaxOffsetDays) Err($"{at}.dueOffsetDays", TaskRuleMessages.DueOffsetRange);
            if (CheckWhen(module, t.When, userIds) is { } taskWhen) Err($"{at}.when", taskWhen);
        }

        if (input.Repeat == TaskRuleRepeat.EveryTime && input.Trigger!.On == "onCreate") Err("repeat", TaskRuleMessages.CreateCannotRepeat);

        if (errors.Count == 0)
        {
            var count = (await rules.ListByModuleAsync(module.Id, ct)).Count(r => r.Key != selfKey);
            if (count >= MaxRulesPerModule) Err("moduleKey", TaskRuleMessages.TooManyRules);
        }
        return errors.Count > 0 ? ServiceResult.Invalid(errors) : null;
    }

    private string? CheckWhen(Module module, JsonElement? when, IReadOnlySet<string> userIds)
    {
        if (when is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }) return null;
        if (when.Value.ValueKind != JsonValueKind.Object) return "條件格式錯誤";
        var parsed = ConditionParser.Parse(when.Value, module, registry, ConditionUsage.Rule, userIds);
        return parsed.Error is null ? null : $"條件不正確：{parsed.Error}";
    }

    private static void CheckTrigger(TaskRuleInput input, IReadOnlyDictionary<long, Field> fields, Action<string, string> err)
    {
        var t = input.Trigger;
        if (!TriggerKinds.Contains(t.On)) { err("trigger.on", TaskRuleMessages.BadTriggerOn); return; }
        if (t.On == "onUpdateMatch" && input.When is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
            err("when", TaskRuleMessages.UpdateMatchNeedsWhen);
        if (t.On != "onFieldBecomes") return;

        if (t.FieldId is not { } id || !fields.TryGetValue(id, out var field) || field.Status != FieldStatus.Active
            || field.Type is not (FieldType.Enum or FieldType.Boolean))
        {
            err("trigger.field", TaskRuleMessages.TriggerFieldKind);
            return;
        }
        var value = t.Value;
        var ok = field.Type == FieldType.Boolean
            ? value is { ValueKind: JsonValueKind.True or JsonValueKind.False }
            : value is { ValueKind: JsonValueKind.String } v && v.GetString() is { } s && s.StartsWith("opt_", StringComparison.Ordinal)
                && long.TryParse(s.AsSpan(4), out var optionId) && field.Options.Any(o => o.Id == optionId);
        if (!ok) err("trigger.value", TaskRuleMessages.TriggerValueInvalid);
    }

    /// <summary>多語系文字：每個語系的值不超過上限、不能有不支援的語系；必填時 zh-TW 解析後不能是空的。</summary>
    private static void CheckText(I18nText? text, string key, bool required, int max, string tooLong, Action<string, string> err)
    {
        if (text is null || text.Values.Count == 0)
        {
            if (required) err(key, TaskMessages.TitleRequired);
            return;
        }
        if (text.Values.Keys.FirstOrDefault(k => !I18nText.SupportedLocales.Contains(k)) is { } unknown)
        {
            err(key, $"不支援的語系：{unknown}");
            return;
        }
        if (text.Values.FirstOrDefault(kv => kv.Value is null) is { Key: { } nullKey })
        {
            err(key, $"語系 {nullKey} 的文字不能是空值");
            return;
        }
        if (text.Values.Values.Any(v => v.Trim().Length > max)) { err(key, tooLong); return; }
        if (required && string.IsNullOrWhiteSpace(text.Resolve(I18nText.FallbackLocale))) err(key, TaskMessages.TitleRequired);
    }
}
