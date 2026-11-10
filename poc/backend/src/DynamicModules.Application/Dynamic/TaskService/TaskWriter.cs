using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.TaskService;

public sealed record NewTask(string Title, string? Description, string? AssigneeUserId, DateOnly? DueDate,
    long? ModuleId, long? RecordId, string? RecordTitle, long? RuleId, string? RuleTaskCode, string? CreatedBy);

/// <summary>Set* 為 false 代表沒送、不改；為 true 時值可以是 null（清除）。Title、Status 為 null 代表不改。</summary>
public sealed record TaskEdit(string? Title, bool SetDescription, string? Description, bool SetAssignee, string? AssigneeUserId,
    bool SetDueDate, DateOnly? DueDate, TaskItemStatus? Status);

/// <summary>
/// 任務寫入的唯一出口：系統記錄、參與者、通知事件都從這裡出去。待送的通知在 <see cref="FlushAsync"/> 送出後清空，不跨請求保留。
/// </summary>
public sealed class TaskWriter(ICurrentUser user, IBusinessTime time, ITaskRepository tasks, ITaskNotifier notifier, IUnitOfWork uow)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<TaskChange> _pending = [];

    /// <summary>新增；記 created 系統記錄、建立者與負責人參與者，有負責人時記一筆 Assigned 變更。</summary>
    public TaskItem Create(NewTask input)
    {
        var now = time.Now;
        var task = new TaskItem
        {
            Title = input.Title, Description = input.Description, AssigneeUserId = input.AssigneeUserId, DueDate = input.DueDate,
            ModuleId = input.ModuleId, RecordId = input.RecordId, RecordTitleSnapshot = input.RecordTitle,
            RuleId = input.RuleId, RuleTaskCode = input.RuleTaskCode, CreatedBy = input.CreatedBy, CreatedTime = now
        };
        AddSystemEntry(task, TaskSystemEvents.Created, input.CreatedBy, null);
        if (input.CreatedBy is not null) AddParticipant(task, input.CreatedBy, TaskParticipantReason.Creator);
        if (input.AssigneeUserId is not null)
        {
            AddParticipant(task, input.AssigneeUserId, TaskParticipantReason.Assignee);
            _pending.Add(new TaskChange(TaskChangeKind.Assigned, task, user.UserId, input.AssigneeUserId));
        }
        tasks.Add(task);
        return task;
    }

    /// <summary>人手動修改：規則任務改到負責人或到期日時設 Overridden 旗標，改狀態時清掉 CancelledByRule。</summary>
    public Task ApplyAsync(TaskItem task, TaskEdit edit, CancellationToken ct)
    {
        var actor = user.UserId;
        var changed = false;
        if (edit.Title is not null && edit.Title != task.Title)
        {
            task.Title = edit.Title;
            changed = true;
        }
        if (edit.SetDescription && edit.Description != task.Description)
        {
            task.Description = edit.Description;
            changed = true;
        }
        if (edit.SetAssignee && edit.AssigneeUserId != task.AssigneeUserId)
        {
            ChangeAssignee(task, edit.AssigneeUserId, actor);
            if (task.RuleId is not null) task.AssigneeOverridden = true;
            changed = true;
        }
        if (edit.SetDueDate && edit.DueDate != task.DueDate)
        {
            ChangeDue(task, edit.DueDate, actor);
            if (task.RuleId is not null) task.DueOverridden = true;
            changed = true;
        }
        if (edit.Status is { } status && status != task.Status)
        {
            ChangeStatus(task, status, actor, TaskSystemEvents.StatusChanged);
            task.CancelledByRule = false;
            changed = true;
        }
        if (changed) Touch(task, actor);
        return Task.CompletedTask;
    }

    /// <summary>規則同步（未完成、沒手動改過的才會被呼叫）：系統記錄的作者為 null，不設 Overridden 旗標。</summary>
    public Task SyncFromRuleAsync(TaskItem task, bool setAssignee, string? assignee, bool setDue, DateOnly? due, CancellationToken ct)
    {
        var changed = false;
        if (setAssignee && assignee != task.AssigneeUserId)
        {
            ChangeAssignee(task, assignee, null);
            changed = true;
        }
        if (setDue && due != task.DueDate)
        {
            ChangeDue(task, due, null);
            changed = true;
        }
        if (changed) Touch(task, null);
        return Task.CompletedTask;
    }

    /// <summary>系統取消（記錄刪除、規則條件不再成立）；只有條件不成立的取消才能由規則恢復。</summary>
    public void CancelBySystem(TaskItem task, string systemEvent)
    {
        if (!task.IsOpen) return;
        ChangeStatus(task, TaskItemStatus.Cancelled, null, systemEvent);
        task.CancelledByRule = systemEvent == TaskSystemEvents.ConditionUnmet;
        Touch(task, null);
    }

    /// <summary>規則條件恢復成立：把規則取消的任務改回待辦。</summary>
    public void RestoreByRule(TaskItem task)
    {
        if (!task.CancelledByRule || task.Status != TaskItemStatus.Cancelled) return;
        ChangeStatus(task, TaskItemStatus.Todo, null, TaskSystemEvents.ConditionRestored);
        task.CancelledByRule = false;
        Touch(task, null);
    }

    /// <summary>人寫的留言：內文 @ 到的啟用中使用者加為 Mentioned 參與者，記 Mentioned（每人一筆）與 Commented（帶被 @ 的清單）。</summary>
    public Task<TaskComment> AddCommentAsync(TaskItem task, string body, IReadOnlySet<string> activeUserIds, CancellationToken ct)
    {
        var actor = user.UserId;
        var comment = new TaskComment
        {
            Kind = TaskCommentKind.Comment, AuthorUserId = actor, Body = body, CreatedTime = time.Now
        };
        task.Comments.Add(comment);
        var mentioned = TaskMentions.UserIds(body).Where(activeUserIds.Contains).ToList();
        foreach (var id in mentioned)
        {
            AddParticipant(task, id, TaskParticipantReason.Mentioned);
            _pending.Add(new TaskChange(TaskChangeKind.Mentioned, task, actor, id));
        }
        _pending.Add(new TaskChange(TaskChangeKind.Commented, task, actor, null, mentioned));
        return Task.FromResult(comment);
    }

    /// <summary>撤回：內文保留在資料庫，回應時不再輸出。</summary>
    public void Withdraw(TaskComment comment)
    {
        if (comment.Withdrawn) return;
        comment.Withdrawn = true;
        comment.WithdrawnBy = user.UserId;
        comment.WithdrawnTime = time.Now;
    }

    public Task EnsureParticipantAsync(TaskItem task, string userId, TaskParticipantReason reason, CancellationToken ct)
    {
        AddParticipant(task, userId, reason);
        return Task.CompletedTask;
    }

    /// <summary>Commit（新任務取得 Id）→ 送出待送的通知 → 再 Commit；送完清空。</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        await uow.CommitAsync(ct);
        if (_pending.Count == 0) return;
        var batch = _pending.ToList();
        _pending.Clear();
        await notifier.NotifyAsync(batch, ct);
        await uow.CommitAsync(ct);
    }

    private void ChangeAssignee(TaskItem task, string? to, string? author)
    {
        var from = task.AssigneeUserId;
        task.AssigneeUserId = to;
        AddSystemEntry(task, TaskSystemEvents.AssigneeChanged, author, FromTo(from, to));
        // 舊負責人保留為參與者（他可能還在留言串裡）
        if (to is not null)
        {
            AddParticipant(task, to, TaskParticipantReason.Assignee);
            _pending.Add(new TaskChange(TaskChangeKind.Assigned, task, user.UserId, to));
        }
    }

    private void ChangeDue(TaskItem task, DateOnly? to, string? author)
    {
        var from = task.DueDate;
        task.DueDate = to;
        AddSystemEntry(task, TaskSystemEvents.DueChanged, author, FromTo(DateText(from), DateText(to)));
    }

    private void ChangeStatus(TaskItem task, TaskItemStatus to, string? author, string systemEvent)
    {
        var from = task.Status;
        task.Status = to;
        task.CompletedTime = to == TaskItemStatus.Done ? time.Now : null;
        AddSystemEntry(task, systemEvent, author, FromTo(StatusText(from), StatusText(to)));
        if (to == TaskItemStatus.Done) _pending.Add(new TaskChange(TaskChangeKind.Completed, task, user.UserId));
        if (to == TaskItemStatus.Cancelled) _pending.Add(new TaskChange(TaskChangeKind.Cancelled, task, user.UserId));
    }

    private void Touch(TaskItem task, string? actor)
    {
        task.ModifiedBy = actor;
        task.ModifiedTime = time.Now;
    }

    private void AddSystemEntry(TaskItem task, string systemEvent, string? author, string? data) =>
        task.Comments.Add(new TaskComment
        {
            Kind = TaskCommentKind.System, AuthorUserId = author, Body = "", SystemEvent = systemEvent, SystemData = data,
            CreatedTime = time.Now
        });

    private static void AddParticipant(TaskItem task, string userId, TaskParticipantReason reason)
    {
        if (task.Participants.Any(p => p.UserId == userId && p.Reason == reason)) return;
        task.Participants.Add(new TaskParticipant { TaskId = task.Id, UserId = userId, Reason = reason });
    }

    private static string FromTo(string? from, string? to) =>
        JsonSerializer.Serialize(new Dictionary<string, string?> { ["from"] = from, ["to"] = to }, Json);

    private static string? DateText(DateOnly? d) => d?.ToString("yyyy-MM-dd");

    /// <summary>與 API 的序列化一致（camelCase）。</summary>
    internal static string StatusText(TaskItemStatus s) => JsonNamingPolicy.CamelCase.ConvertName(s.ToString());
}
