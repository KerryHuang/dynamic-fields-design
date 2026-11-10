using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.TaskService;

public sealed record TaskInput(string Title, string? Description, string? AssigneeUserId, DateOnly? DueDate);

/// <summary>不含任何記錄欄位值；Record 依檢視者的權限遮蔽（設計 §7）。</summary>
public sealed record TaskDetailResponse(
    long Id, string Title, string? Description, TaskItemStatus Status, string? AssigneeUserId, string? AssigneeName, bool AssigneeActive,
    DateOnly? DueDate, bool Overdue, TaskRecordRef? Record, string? CreatedBy, string? CreatedByName, DateTime CreatedTime,
    DateTime? CompletedTime, string? RuleKey, bool RuleDeleted, bool CanEdit, bool CanComment, bool CanDelete, bool Frozen);

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
}
