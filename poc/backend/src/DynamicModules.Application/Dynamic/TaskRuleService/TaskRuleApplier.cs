using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.TaskRuleService;

/// <summary>
/// 把一條規則套到一筆記錄（設計 §5.2、§5.5）：產生一批任務，或對最新一批做個別任務條件與同步。
/// 只把變更交給 <see cref="TaskWriter"/>，由呼叫端 Flush；不保留跨呼叫的狀態。
/// </summary>
public sealed class TaskRuleApplier(ITaskRuleRepository rules, ITaskRepository tasks, TaskWriter writer, FieldTypeRegistry registry,
    IBusinessTime time, ICurrentUser user, ICurrentLocale locale)
{
    /// <summary>寫 TaskRuleRun(occurrence)，並為條件成立的每個任務範本建立任務；標題與說明依觸發者語系解析。</summary>
    public Task GenerateAsync(TaskRule rule, Module module, long recordId, int occurrence, string recordTitle,
        IReadOnlyDictionary<string, JsonElement?> values, IReadOnlySet<string> activeUsers, IReadOnlySet<string> userIds, CancellationToken ct)
    {
        rules.AddRun(new TaskRuleRun
        {
            RuleId = rule.Id, RecordId = recordId, Occurrence = occurrence, CreatedTime = time.Now, TriggeredBy = user.UserId
        });
        var today = time.Today;
        foreach (var def in rule.Tasks.OrderBy(t => t.SortOrder))
            if (TaskConditions.Holds(def.WhenJson, module, values, registry, today, userIds))
                Create(rule, def, module, recordId, occurrence, recordTitle, values, activeUsers);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 只看第 occurrence 批：個別任務條件由不成立變成立時補產生或恢復規則取消的任務，由成立變不成立時取消未完成的任務；
    /// 未完成、沒被人改過的任務跟著負責人欄位、到期基準同步。Done 與人手動取消的不動，代碼已不在規則裡的任務不動。
    /// </summary>
    public async Task ReconcileAsync(TaskRule rule, Module module, long recordId, int occurrence, string recordTitle,
        IReadOnlyDictionary<string, JsonElement?>? before, IReadOnlyDictionary<string, JsonElement?> after,
        IReadOnlySet<string> activeUsers, IReadOnlySet<string> userIds, CancellationToken ct)
    {
        var existing = await tasks.ListByRuleOccurrenceAsync(rule.Id, recordId, occurrence, ct);
        var today = time.Today;
        foreach (var def in rule.Tasks.OrderBy(t => t.SortOrder))
        {
            var task = existing.FirstOrDefault(t => t.RuleTaskCode == def.Code);
            var was = before is not null && TaskConditions.Holds(def.WhenJson, module, before, registry, today, userIds);
            var now = TaskConditions.Holds(def.WhenJson, module, after, registry, today, userIds);
            if (!was && now)
            {
                if (task is null)
                {
                    Create(rule, def, module, recordId, occurrence, recordTitle, after, activeUsers);
                    continue;
                }
                writer.RestoreByRule(task);
            }
            else if (was && !now && task is { IsOpen: true })
            {
                writer.CancelBySystem(task, TaskSystemEvents.ConditionUnmet);
            }

            if (task is not { IsOpen: true }) continue;
            var setAssignee = !task.AssigneeOverridden && TaskRuleEvaluator.Changed(def.AssigneeFieldId, before, after);
            var setDue = !task.DueOverridden && TaskRuleEvaluator.Changed(def.DueFieldId, before, after);
            if (setAssignee || setDue)
                await writer.SyncFromRuleAsync(task, setAssignee, TaskRuleEvaluator.Assignee(def, after, activeUsers),
                    setDue, TaskRuleEvaluator.DueDate(def, module, after), ct);
        }
    }

    private void Create(TaskRule rule, TaskRuleTask def, Module module, long recordId, int occurrence, string recordTitle,
        IReadOnlyDictionary<string, JsonElement?> values, IReadOnlySet<string> activeUsers)
    {
        var description = def.DescriptionI18n?.Resolve(locale.Locale);
        writer.Create(new NewTask(def.TitleI18n.Resolve(locale.Locale), string.IsNullOrWhiteSpace(description) ? null : description,
            TaskRuleEvaluator.Assignee(def, values, activeUsers), TaskRuleEvaluator.DueDate(def, module, values),
            module.Id, recordId, recordTitle, rule.Id, occurrence, def.Code, null));
    }
}
