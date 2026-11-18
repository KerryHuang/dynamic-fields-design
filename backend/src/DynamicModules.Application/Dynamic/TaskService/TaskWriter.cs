using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.TaskService;

public sealed record NewTask(string Title, string? Description, string? AssigneeUserId, DateOnly? DueDate,
    long? ModuleId, long? RecordId, string? RecordTitle, long? RuleId, int? RuleOccurrence, string? RuleTaskCode, string? CreatedBy,
    long? ParentTaskId = null);

/// <summary>
/// Set* 為 false 代表沒送、不改；為 true 時值可以是 null（清除）。Title、Status 為 null 代表不改。
/// Cascade：父任務改成完成、還有未完成的子任務時，子任務一併完成或一併取消（看板設計 §10.7.2）；其他情況忽略。
/// </summary>
public sealed record TaskEdit(string? Title, bool SetDescription, string? Description, bool SetAssignee, string? AssigneeUserId,
    bool SetDueDate, DateOnly? DueDate, TaskItemStatus? Status, TaskCascade? Cascade = null);

/// <summary>父任務改成完成時，未完成的子任務要怎麼處理（PATCH／move 的 cascade：complete、cancel）。</summary>
public enum TaskCascade { Complete, Cancel }

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
            RuleId = input.RuleId, RuleOccurrence = input.RuleOccurrence, RuleTaskCode = input.RuleTaskCode, CreatedBy = input.CreatedBy, CreatedTime = now,
            ParentTaskId = input.ParentTaskId
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

    /// <summary>
    /// 人手動修改：規則任務改到負責人或到期日時設 Overridden 旗標，改狀態時清掉 CancelledByRule、CancelledByParent。
    /// 改狀態另依父子關係連動（看板設計 §10.7.4，同一交易）；不能寫入時回 409、不改任何值：
    /// 父任務已取消時重開子任務（ST-Q3）、父任務改成完成還有未完成的子任務卻沒帶 cascade。
    /// </summary>
    public async Task<ServiceResult?> ApplyAsync(TaskItem task, TaskEdit edit, CancellationToken ct)
    {
        var actor = user.UserId;
        TaskItem? parent = null;
        List<TaskItem> children = [];
        if (edit.Status is { } target && target != task.Status)
        {
            if (task.ParentTaskId is { } parentId)
            {
                // 讀取時父任務查不到（資料不一致）就當成獨立任務，不連動
                parent = await tasks.GetAsync(parentId, ct);
                if (parent is { Status: TaskItemStatus.Cancelled } && !task.IsOpen && IsOpen(target))
                    return ServiceResult.Conflict(MessageRef.Literal(TaskMessages.ParentCancelledReopen), "parentCancelled");
            }
            else if (target is TaskItemStatus.Done or TaskItemStatus.Cancelled || task.Status == TaskItemStatus.Cancelled)
            {
                children = await tasks.ListChildrenAsync(task.Id, ct);
                var open = children.Count(c => c.IsOpen);
                if (target == TaskItemStatus.Done && open > 0 && edit.Cascade is null)
                    return ServiceResult.Conflict(MessageRef.Literal(TaskMessages.SubtasksOpen(open)), "subtasksOpen", new { count = open });
            }
        }

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
            var from = task.Status;
            ChangeStatus(task, status, actor, TaskSystemEvents.StatusChanged);
            task.CancelledByRule = false;
            task.CancelledByParent = false;
            changed = true;
            if (parent is not null) await FollowSubtaskAsync(parent, task, from, ct);
            else if (children.Count > 0) CascadeToChildren(task, children, from, edit.Cascade, actor);
        }
        if (changed) Touch(task, actor);
        return null;
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

    /// <summary>
    /// 系統取消（記錄刪除、規則條件不再成立）；只有條件不成立的取消才能由規則恢復。條件不成立的父任務連帶取消未完成的子任務；
    /// 記錄刪除不連動（ST-T6）：子任務掛同一筆記錄，由 <see cref="TaskRecordStage"/> 逐筆以 recordDeleted 取消、不設 CancelledByParent。
    /// </summary>
    public async Task CancelBySystemAsync(TaskItem task, string systemEvent, CancellationToken ct)
    {
        if (!task.IsOpen) return;
        var from = task.Status;
        ChangeStatus(task, TaskItemStatus.Cancelled, null, systemEvent);
        task.CancelledByRule = systemEvent == TaskSystemEvents.ConditionUnmet;
        Touch(task, null);
        if (systemEvent != TaskSystemEvents.RecordDeleted && task.ParentTaskId is null)
            CascadeToChildren(task, await tasks.ListChildrenAsync(task.Id, ct), from, null, null);
    }

    /// <summary>規則條件恢復成立：把規則取消的任務改回待辦，因它取消的子任務一併恢復成待辦。</summary>
    public async Task RestoreByRuleAsync(TaskItem task, CancellationToken ct)
    {
        if (!task.CancelledByRule || task.Status != TaskItemStatus.Cancelled) return;
        ChangeStatus(task, TaskItemStatus.Todo, null, TaskSystemEvents.ConditionRestored);
        task.CancelledByRule = false;
        Touch(task, null);
        if (task.ParentTaskId is null)
            CascadeToChildren(task, await tasks.ListChildrenAsync(task.Id, ct), TaskItemStatus.Cancelled, null, null);
    }

    /// <summary>
    /// 父任務留言串的稽核記錄（subtaskAdded、subtaskDeleted，作者是操作者）：只加記錄、不 Touch，所以不改父任務版本，
    /// 別人拿舊版本拖父卡不會 409（ST-T8）。子任務要先有 Id（新增時先 Flush）。
    /// </summary>
    public void AddSubtaskEntry(TaskItem parent, string systemEvent, TaskItem subtask) =>
        AddSystemEntry(parent, systemEvent, user.UserId, JsonSerializer.Serialize(
            new Dictionary<string, object> { ["subtaskId"] = subtask.Id, ["subtaskTitle"] = subtask.Title }, Json));

    /// <summary>只留一筆系統記錄（作者為 null），不改任務本身；例如規則再次符合、但上一輪任務尚未完成。</summary>
    public void AddSystemEntry(TaskItem task, string systemEvent) => AddSystemEntry(task, systemEvent, null, null);

    /// <summary>人寫的留言：內文 @ 到的啟用中使用者加為 Mentioned 參與者，記 Mentioned（每人一筆）與 Commented（帶被 @ 的清單）。</summary>
    public async Task<TaskComment> AddCommentAsync(TaskItem task, string body, IReadOnlySet<string> activeUserIds,
        IReadOnlyList<TaskCommentAttachment> attachments, CancellationToken ct)
    {
        var actor = user.UserId;
        var comment = new TaskComment
        {
            Kind = TaskCommentKind.Comment, AuthorUserId = actor, Body = body, CreatedTime = time.Now, Attachments = [.. attachments]
        };
        task.Comments.Add(comment);
        var mentioned = TaskMentions.UserIds(body).Where(activeUserIds.Contains).ToList();
        // 參與者以資料庫為準：另一個請求可能在這個請求讀到任務之後才把同一個人寫成 Mentioned，存在就略過，不撞主鍵
        var stored = task.Id == 0
            ? new HashSet<string>()
            : (await tasks.ListParticipantsAsync(task.Id, ct))
                .Where(p => p.Reason == TaskParticipantReason.Mentioned).Select(p => p.UserId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in mentioned)
        {
            if (!stored.Contains(id)) AddParticipant(task, id, TaskParticipantReason.Mentioned);
            _pending.Add(new TaskChange(TaskChangeKind.Mentioned, task, actor, id));
        }
        _pending.Add(new TaskChange(TaskChangeKind.Commented, task, actor, null, mentioned));
        return comment;
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

    /// <summary>
    /// 子任務改了狀態後帶動父任務（§10.7.4 第 1、2、4 項；不檢查操作者對父任務的權限，T25）：開工時待辦的父任務改成進行中、
    /// 重開時完成的父任務改回進行中（系統，寫 bySubtask）；最後一個未完成的子任務收尾、且至少一個已完成時寫 subtasksAllDone、依 A40 通知。
    /// 父任務被帶動時不再反過來連動其他子任務（ST-T11）。
    /// </summary>
    private async Task FollowSubtaskAsync(TaskItem parent, TaskItem child, TaskItemStatus from, CancellationToken ct)
    {
        var to = child.Status;
        var reopened = !IsOpen(from) && IsOpen(to);
        if ((to == TaskItemStatus.InProgress && parent.Status == TaskItemStatus.Todo) || (reopened && parent.Status == TaskItemStatus.Done))
        {
            var parentFrom = parent.Status;
            SetStatus(parent, TaskItemStatus.InProgress, null, TaskSystemEvents.BySubtask, new Dictionary<string, object?>
            {
                ["from"] = StatusText(parentFrom), ["to"] = StatusText(TaskItemStatus.InProgress), ["subtaskId"] = child.Id, ["subtaskTitle"] = child.Title
            });
            Touch(parent, null);
            return;
        }
        if (!IsOpen(from) || IsOpen(to) || !parent.IsOpen) return;
        // 同一個 DbContext 會回已追蹤的同一個子任務實體（狀態已是改過的）；仍以 child 為準，不依賴這個行為
        var siblings = (await tasks.ListChildrenAsync(parent.Id, ct)).Where(c => c.Id != child.Id).Append(child).ToList();
        if (siblings.Any(c => c.IsOpen) || siblings.All(c => c.Status != TaskItemStatus.Done)) return;
        // 只加系統記錄、不改父任務本身，同 subtaskAdded 不 Touch（不改父任務版本）
        AddSystemEntry(parent, TaskSystemEvents.SubtasksAllDone, null, JsonSerializer.Serialize(
            new Dictionary<string, object> { ["subtaskId"] = child.Id, ["subtaskTitle"] = child.Title }, Json));
        _pending.Add(new TaskChange(TaskChangeKind.SubtasksDone, parent, user.UserId));
    }

    /// <summary>
    /// 父任務改了狀態後帶動子任務（§10.7.4 第 3、6、7 項；子任務寫 byParent）。改成完成：cascade=complete／cancel 時未完成的子任務一併完成／取消，
    /// 作者與 ModifiedBy 是選連動方式的操作者（chooser），不設 CancelledByParent（ST-T5）；從取消直接改成完成時旗標一律清掉、子任務不恢復。
    /// 改成取消：未完成的子任務取消並設 CancelledByParent（系統）。從取消恢復：CancelledByParent 的子任務改回待辦、清旗標（系統）。
    /// 子任務改狀態不再連動父任務（ST-T11）。完成與取消照 A13 各自通知（ChangeStatus）。
    /// </summary>
    private void CascadeToChildren(TaskItem parent, IReadOnlyList<TaskItem> children, TaskItemStatus from, TaskCascade? cascade, string? chooser)
    {
        var to = parent.Status;
        if (to == TaskItemStatus.Done)
        {
            if (cascade is { } how)
                foreach (var c in children.Where(c => c.IsOpen))
                {
                    ChangeByParent(c, parent, how == TaskCascade.Complete ? TaskItemStatus.Done : TaskItemStatus.Cancelled, chooser);
                    c.CancelledByParent = false;
                }
            if (from == TaskItemStatus.Cancelled)
                foreach (var c in children) c.CancelledByParent = false;
        }
        else if (to == TaskItemStatus.Cancelled)
        {
            foreach (var c in children.Where(c => c.IsOpen))
            {
                ChangeByParent(c, parent, TaskItemStatus.Cancelled, null);
                c.CancelledByParent = true;
            }
        }
        else if (from == TaskItemStatus.Cancelled)
        {
            foreach (var c in children.Where(c => c is { CancelledByParent: true, Status: TaskItemStatus.Cancelled }))
            {
                ChangeByParent(c, parent, TaskItemStatus.Todo, null);
                c.CancelledByParent = false;
            }
        }
    }

    private void ChangeByParent(TaskItem child, TaskItem parent, TaskItemStatus to, string? author)
    {
        var from = child.Status;
        SetStatus(child, to, author, TaskSystemEvents.ByParent, new Dictionary<string, object?>
        {
            ["from"] = StatusText(from), ["to"] = StatusText(to), ["parentId"] = parent.Id, ["parentTitle"] = parent.Title
        });
        Touch(child, author);
    }

    private static bool IsOpen(TaskItemStatus s) => s is TaskItemStatus.Todo or TaskItemStatus.InProgress;

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

    private void ChangeStatus(TaskItem task, TaskItemStatus to, string? author, string systemEvent) =>
        SetStatus(task, to, author, systemEvent, new Dictionary<string, object?> { ["from"] = StatusText(task.Status), ["to"] = StatusText(to) });

    private void SetStatus(TaskItem task, TaskItemStatus to, string? author, string systemEvent, Dictionary<string, object?> data)
    {
        task.Status = to;
        task.CompletedTime = to == TaskItemStatus.Done ? time.Now : null;
        AddSystemEntry(task, systemEvent, author, JsonSerializer.Serialize(data, Json));
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
