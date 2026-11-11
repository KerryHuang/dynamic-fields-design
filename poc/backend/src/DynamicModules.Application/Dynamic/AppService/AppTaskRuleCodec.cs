using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TaskRuleService;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// 任務規則在套件（代號）與資料庫（Id）之間互轉（任務管理設計 §5.8）。條件 JSON 和連動事件共用 <see cref="AppEffectsCodec"/> 的轉換。
/// 解析不了的引用累積成錯誤，不丟例外也不猜；呼叫端決定是不匯出並警告（匯出）或略過並警告（開啟時欄位被排除）。
/// </summary>
public static class AppTaskRuleCodec
{
    // ---- 匯出方向：Id → 代號 ----

    public static (AppTaskRule? Rule, List<string> Errors) ToPackage(TaskRule r, string moduleCode, AppReportCodec.Names n)
    {
        var errors = new List<string>();
        string Field(long id)
        {
            var name = n.Field(id);
            if (name is null) errors.Add("引用了套件外或已停用的欄位");
            return name ?? "";
        }
        JsonElement? When(string? json) =>
            TaskRuleMapping.ParseWhen(json) is { } w ? AppEffectsCodec.ExportCondition(w, n, errors) : null;

        AppTaskTrigger trigger;
        if (r.Trigger.On == "onFieldBecomes" && r.Trigger.FieldId is { } fieldId)
        {
            var value = r.Trigger.Value;
            // 下拉的值 opt_<選項 Id> 換成 opt_<選項代號>；是非欄位的 true／false 原樣
            if (value is { ValueKind: JsonValueKind.String } v && AppPackageValidator.OptionCodeOf(v.GetString()) is { } raw)
            {
                var code = long.TryParse(raw, out var optionId) ? n.Option(fieldId, optionId) : null;
                if (code is null) errors.Add("觸發值引用了套件外或已停用的選項");
                value = JsonSerializer.SerializeToElement($"opt_{code}");
            }
            trigger = new AppTaskTrigger(r.Trigger.On, Field(fieldId), value);
        }
        else trigger = new AppTaskTrigger(r.Trigger.On, null, null);

        var tasks = r.Tasks.OrderBy(t => t.SortOrder).ThenBy(t => t.Id).Select(t => new AppTaskRuleTask(
            t.Code, t.TitleI18n, t.DescriptionI18n, Field(t.AssigneeFieldId),
            t.DueFieldId is { } due ? new AppTaskDue(Field(due), t.DueOffsetDays) : null,
            When(t.WhenJson))).ToList();
        var rule = new AppTaskRule(r.Key, r.NameI18n, r.Active, moduleCode,
            r.Repeat == TaskRuleRepeat.EveryTime ? AppTaskRule.EveryTime : null, trigger, When(r.WhenJson), tasks);
        return (errors.Count == 0 ? rule : null, errors.Distinct().ToList());
    }

    // ---- 匯入方向：代號 → Id ----

    /// <summary>模組鍵由呼叫端另外用 <see cref="AppReportCodec.Lookups.ModuleKey"/> 取得；這裡只檢查它解析得到。</summary>
    public static (TaskRuleInput? Input, List<string> Errors) ToInput(AppTaskRule r, AppReportCodec.Lookups l)
    {
        var errors = new List<string>();
        long Field(string? name)
        {
            var id = name is null ? null : l.FieldId(name);
            if (id is null) errors.Add($"引用了套件沒有的欄位 {name}");
            return id ?? 0;
        }
        JsonElement? When(JsonElement? json) =>
            json is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } w ? AppEffectsCodec.ImportCondition(w, l, errors) : null;

        if (l.ModuleKey(r.Module) is null) errors.Add($"引用了套件沒有的模組 {r.Module}");
        var repeat = r.Repeat switch
        {
            null or AppTaskRule.Once => TaskRuleRepeat.Once,
            AppTaskRule.EveryTime => TaskRuleRepeat.EveryTime,
            _ => Fail(TaskRuleRepeat.Once, $"repeat 必須是 once 或 everyTime（{r.Repeat}）")
        };

        TaskRuleTriggerInput trigger;
        if (r.Trigger.On == "onFieldBecomes" && r.Trigger.Field is { } field)
        {
            var value = r.Trigger.Value;
            if (value is { ValueKind: JsonValueKind.String } v && AppPackageValidator.OptionCodeOf(v.GetString()) is { } code)
            {
                var module = TemplateQualified.ModuleOf(field);
                var optionId = l.OptionId($"{module}.{code}");
                if (optionId is null) errors.Add($"引用了套件沒有的選項 {code}（模組 {module}）");
                value = JsonSerializer.SerializeToElement($"opt_{optionId}");
            }
            trigger = new TaskRuleTriggerInput(r.Trigger.On, Field(field), value);
        }
        else trigger = new TaskRuleTriggerInput(r.Trigger.On, null, null);

        var tasks = r.Tasks.Select(t => new TaskRuleTaskInput(
            t.Code, t.Title, t.Description, Field(t.Assignee),
            t.Due is { } due ? Field(due.Field) : null, t.Due?.OffsetDays ?? 0, When(t.When))).ToList();
        var input = new TaskRuleInput(r.Code, r.Name, r.Active, repeat, trigger, When(r.When), tasks);
        return errors.Count == 0 ? (input, []) : (null, errors.Distinct().ToList());

        T Fail<T>(T fallback, string message)
        {
            errors.Add(message);
            return fallback;
        }
    }
}
