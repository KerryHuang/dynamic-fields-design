using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.TaskService;

public sealed record TaskInput(string Title, string? Description, string? AssigneeUserId, DateOnly? DueDate);

/// <summary>不含任何記錄欄位值；Record 依檢視者的權限遮蔽（設計 §7）。</summary>
public sealed record TaskDetailResponse(
    long Id, string Title, string? Description, TaskItemStatus Status, string? AssigneeUserId, string? AssigneeName, bool AssigneeActive,
    DateOnly? DueDate, bool Overdue, TaskRecordRef? Record, string? CreatedBy, string? CreatedByName, DateTime CreatedTime,
    DateTime? CompletedTime, string? RuleKey, bool RuleDeleted, bool CanEdit, bool CanComment, bool CanDelete, bool Frozen);

public sealed record TaskSummaryResponse(
    long Id, string Title, TaskItemStatus Status, string? AssigneeUserId, string? AssigneeName, bool AssigneeActive,
    DateOnly? DueDate, bool Overdue, TaskRecordRef? Record, bool FromRule, bool CanEdit);

/// <summary>ManualRules：t1a 一律是空清單，任務規則上線後才有值。</summary>
public sealed record RecordTasksResponse(IReadOnlyList<TaskSummaryResponse> Items, bool CanAdd, IReadOnlyList<ManualRuleResponse> ManualRules);

public sealed record ManualRuleResponse(string Key, I18nText Name, TaskRuleRepeat Repeat, bool Applied, bool Applicable, bool PreviousOpen);

public sealed record TaskSummaryCounts(int DueToday, int Overdue);

internal static class TaskViews
{
    public static bool Overdue(TaskItem t, DateOnly today) => t.IsOpen && t.DueDate is { } d && d < today;

    public static TaskDetailResponse ToDetail(TaskItem t, TaskPermissions p, IReadOnlyDictionary<string, AppUser> users,
        DateOnly today, string? ruleKey)
    {
        var assignee = t.AssigneeUserId is null ? null : users.GetValueOrDefault(t.AssigneeUserId);
        var creator = t.CreatedBy is null ? null : users.GetValueOrDefault(t.CreatedBy);
        return new TaskDetailResponse(
            t.Id, t.Title, t.Description, t.Status, t.AssigneeUserId, assignee?.DisplayName, assignee is { IsActive: true },
            t.DueDate, Overdue(t, today), p.Record, t.CreatedBy, creator?.DisplayName, t.CreatedTime, t.CompletedTime,
            ruleKey, t.RuleId is not null && ruleKey is null, p.CanEdit, p.CanComment, p.CanDelete, p.Frozen);
    }

    public static TaskSummaryResponse ToSummary(TaskItem t, TaskPermissions p, IReadOnlyDictionary<string, AppUser> users, DateOnly today)
    {
        var assignee = t.AssigneeUserId is null ? null : users.GetValueOrDefault(t.AssigneeUserId);
        return new TaskSummaryResponse(t.Id, t.Title, t.Status, t.AssigneeUserId, assignee?.DisplayName, assignee is { IsActive: true },
            t.DueDate, Overdue(t, today), p.Record, t.RuleId is not null, p.CanEdit);
    }
}
