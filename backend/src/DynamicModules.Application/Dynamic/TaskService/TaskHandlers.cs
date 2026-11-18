using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Tasks;
using System.Text.Json;
using MediatR;

namespace DynamicModules.Application.Dynamic.TaskService;

/// <summary>任務請求共用：輸入驗證與詳細回應組裝。</summary>
public sealed class TaskSupport(ITaskRepository tasks, IPermissionRepository permissions, IBusinessTime time, TaskAccess access,
    TaskRuleService.ManualTaskRules manualRules)
{
    public async Task<Dictionary<string, string[]>> ValidateInputAsync(TaskInput input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (TaskEditParser.TitleError(input.Title?.Trim()) is { } title) errors["title"] = [title];
        if (input.Description is { Length: > TaskEditParser.DescriptionMax }) errors["description"] = [TaskMessages.DescriptionTooLong];
        var assignee = TaskEditParser.NullIfBlank(input.AssigneeUserId);
        if (assignee is not null && !await IsActiveUserAsync(assignee, ct)) errors["assigneeUserId"] = [TaskMessages.AssigneeInvalid];
        return errors;
    }

    public async Task<bool> IsActiveUserAsync(string userId, CancellationToken ct) =>
        await permissions.GetUserAsync(userId, ct) is { IsActive: true };

    /// <summary>父任務另外內嵌全部子任務（看得到父任務就看得到全部子任務，§10.3；仍逐筆評估權限當縱深防禦）與進度。</summary>
    public async Task<TaskDetailResponse> DetailAsync(TaskItem task, TaskPermissions p, CancellationToken ct)
    {
        var children = task.ParentTaskId is null ? await tasks.ListChildrenAsync(task.Id, ct) : [];
        var users = new Dictionary<string, AppUser>();
        if (children.Count > 0) users = (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId);
        else
            foreach (var id in new[] { task.AssigneeUserId, task.CreatedBy }.OfType<string>().Distinct())
                if (await permissions.GetUserAsync(id, ct) is { } u) users[id] = u;
        var ruleKey = task.RuleId is { } ruleId ? await tasks.GetRuleKeyAsync(ruleId, ct) : null;
        var today = time.Today;
        IReadOnlyList<TaskSummaryResponse> subtasks = [];
        if (children.Count > 0)
        {
            var perms = await access.EvaluateManyAsync(children, ct);
            subtasks = children.Select((c, i) => (c, p: perms[i])).Where(x => x.p.CanSee)
                .Select(x => TaskViews.ToSummary(x.c, x.p, users, today, null)).ToList();
        }
        var progress = children.Count == 0 ? null : Progress(children);
        return TaskViews.ToDetail(task, p, users, today, ruleKey, progress, subtasks);
    }

    private static TaskProgress Progress(IReadOnlyCollection<TaskItem> children) => new(
        children.Count(c => c.Status == TaskItemStatus.Done), children.Count(c => c.Status == TaskItemStatus.Cancelled), children.Count);

    /// <summary>頂層任務的進度（子任務沒有子任務，不查）；一次查詢。</summary>
    public Task<Dictionary<long, TaskProgress>> ProgressAsync(IReadOnlyCollection<TaskItem> items, CancellationToken ct) =>
        tasks.SubtaskProgressAsync(items.Where(t => t.ParentTaskId is null).Select(t => t.Id).ToList(), ct);

    /// <summary>記錄上看得到的頂層任務（子任務在父任務詳細裡，ST-Q4），加上可手動套用的規則。</summary>
    public async Task<RecordTasksResponse> RecordTasksAsync(RecordTaskAccess target, CancellationToken ct)
    {
        var items = await tasks.ListByRecordAsync(target.Record.Id, ct);
        var perms = await access.EvaluateManyAsync(items, ct);
        var users = (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId);
        var progress = await ProgressAsync(items, ct);
        var today = time.Today;
        var rows = items.Select((t, i) => (t, p: perms[i])).Where(x => x.p.CanSee)
            .Select(x => TaskViews.ToSummary(x.t, x.p, users, today, progress.GetValueOrDefault(x.t.Id))).ToList();
        return new RecordTasksResponse(rows, target.CanAdd && target.Module.TasksEnabled, await manualRules.ListAsync(target.Module, target.Record, ct));
    }
}

public class CreateTaskHandler(TaskSupport support, TaskWriter writer, TaskAccess access, ICurrentUser user)
    : IRequestHandler<CreateTaskRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateTaskRequest request, CancellationToken ct)
    {
        var input = request.Input;
        if (await support.ValidateInputAsync(input, ct) is { Count: > 0 } errors) return ServiceResult.Invalid(errors);
        var task = writer.Create(new NewTask(input.Title.Trim(), TaskEditParser.NullIfBlank(input.Description),
            TaskEditParser.NullIfBlank(input.AssigneeUserId), input.DueDate, null, null, null, null, null, null, user.UserId));
        await writer.FlushAsync(ct);
        return ServiceResult.Created(await support.DetailAsync(task, await access.EvaluateAsync(task, ct), ct));
    }
}

public class CreateRecordTaskHandler(TaskSupport support, TaskWriter writer, TaskAccess access, ICurrentUser user)
    : IRequestHandler<CreateRecordTaskRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateRecordTaskRequest request, CancellationToken ct)
    {
        var target = await access.ForRecordAsync(request.RecordId, ct);
        if (target is null) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        if (!target.CanAdd) return ServiceResult.Conflict(ModuleGate.ClosedMessage);
        if (!target.Module.TasksEnabled) return ServiceResult.Conflict(MessageRef.Of(MessageText.TasksDisabled));
        var input = request.Input;
        if (await support.ValidateInputAsync(input, ct) is { Count: > 0 } errors) return ServiceResult.Invalid(errors);
        var task = writer.Create(new NewTask(input.Title.Trim(), TaskEditParser.NullIfBlank(input.Description),
            TaskEditParser.NullIfBlank(input.AssigneeUserId), input.DueDate,
            target.Module.Id, target.Record.Id, target.Record.Title, null, null, null, user.UserId));
        await writer.FlushAsync(ct);
        return ServiceResult.Created(await support.DetailAsync(task, await access.EvaluateAsync(task, ct), ct));
    }
}

/// <summary>
/// 新增子任務（看板設計 §10.7.2）：檢查順序 404 → 凍結 409 → 模組關任務 409 → 沒有父任務編輯權 403 → 一層限制 400 →
/// 父任務已完成或取消 409 → 上限 A35 400 → 輸入 400。子任務只能用這支 API 新建、不能搬移（T18），所以「已有子任務的任務不能變成
/// 別人的子任務」不必另外檢查。建立後在父任務留言串記 subtaskAdded（不改父任務版本）。
/// </summary>
public class CreateSubtaskHandler(ITaskRepository tasks, TaskSupport support, TaskWriter writer, TaskAccess access, IPolicyReader policies,
    ICurrentUser user) : IRequestHandler<CreateSubtaskRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateSubtaskRequest request, CancellationToken ct)
    {
        var parent = await tasks.GetAsync(request.ParentId, ct);
        if (parent is null) return ServiceResult.NotFound(TaskMessages.NotFound);
        var p = await access.EvaluateAsync(parent, ct);
        if (!p.CanSee) return ServiceResult.NotFound(TaskMessages.NotFound);
        if (p.Frozen) return ServiceResult.Conflict(ModuleGate.ClosedMessage);
        if (!p.TasksEnabled) return ServiceResult.Conflict(MessageRef.Of(MessageText.TasksDisabled));
        if (!p.CanEdit) return ServiceResult.Forbidden(MessageRef.Literal(TaskMessages.SubtaskNeedsParentEdit));
        if (parent.ParentTaskId is not null) return ServiceResult.Invalid("parent", MessageRef.Literal(TaskMessages.SubtaskOneLevel));
        if (!parent.IsOpen) return ServiceResult.Conflict(MessageRef.Literal(TaskMessages.SubtaskParentClosed), "parentClosed");
        var max = await policies.IntAsync(PolicyCatalog.TaskMaxSubtasksPerTask, null, ct);
        if (await tasks.CountChildrenAsync(parent.Id, ct) >= max)
            return ServiceResult.Invalid("subtasks", MessageRef.Literal(TaskMessages.TooManySubtasks(max)));
        var input = request.Input;
        if (await support.ValidateInputAsync(input, ct) is { Count: > 0 } errors) return ServiceResult.Invalid(errors);
        var child = writer.Create(new NewTask(input.Title.Trim(), TaskEditParser.NullIfBlank(input.Description),
            TaskEditParser.NullIfBlank(input.AssigneeUserId), input.DueDate, parent.ModuleId, parent.RecordId, parent.RecordTitleSnapshot,
            null, null, null, user.UserId, parent.Id));
        await writer.FlushAsync(ct);
        writer.AddSubtaskEntry(parent, TaskSystemEvents.SubtaskAdded, child);
        await writer.FlushAsync(ct);
        return ServiceResult.Created(await support.DetailAsync(child, await access.EvaluateAsync(child, ct), ct));
    }
}

public class UpdateTaskHandler(ITaskRepository tasks, TaskSupport support, TaskWriter writer, TaskAccess access, ITaskUndoTokens undoTokens,
    ICurrentUser user, IBusinessTime time)
    : IRequestHandler<UpdateTaskRequest, ServiceResult>
{
    /// <summary>復原憑證的有效時間（安全限制，T24）。</summary>
    internal static readonly TimeSpan UndoWindow = TimeSpan.FromMinutes(2);

    public async Task<ServiceResult> Handle(UpdateTaskRequest request, CancellationToken ct)
    {
        var task = await tasks.GetAsync(request.Id, ct);
        if (task is null) return ServiceResult.NotFound(TaskMessages.NotFound);
        var p = await access.EvaluateAsync(task, ct);
        if (!p.CanSee) return ServiceResult.NotFound(TaskMessages.NotFound);
        if (p.Frozen) return ServiceResult.Conflict(ModuleGate.ClosedMessage);
        if (!p.CanEdit)
        {
            if (!p.CanReassign) return ServiceResult.Forbidden();
            // 主管身分：只能改負責人，其他欄位一律拒絕（看板設計 §4.3）
            if (request.Patch.ValueKind == JsonValueKind.Object
                && request.Patch.EnumerateObject().Any(x => x.Name != "assigneeUserId"))
                return ServiceResult.Forbidden(MessageRef.Literal(TaskMessages.SupervisorOnlyAssignee));
        }
        var (edit, errors) = TaskEditParser.Parse(request.Patch);
        if (edit is null) return ServiceResult.Invalid(errors);
        if (edit is { SetAssignee: true, AssigneeUserId: { } assignee } && assignee != task.AssigneeUserId
            && !await support.IsActiveUserAsync(assignee, ct))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["assigneeUserId"] = [TaskMessages.AssigneeInvalid] });
        var from = task.AssigneeUserId;
        if (await writer.ApplyAsync(task, edit, ct) is { } refused) return refused;
        await writer.FlushAsync(ct);
        var after = await access.EvaluateAsync(task, ct);
        // 寫入後這位操作者看不到了（主管把任務派到範圍外）：不回任務內容，只給一次性的復原憑證
        if (!after.CanSee)
            return ServiceResult.Success(new TaskHiddenAfterWrite(task.Id, false, undoTokens.Issue(
                new TaskUndoTicket(task.Id, user.UserId, from, task.AssigneeUserId, TaskVersion.Of(task), time.Now + UndoWindow))));
        return ServiceResult.Success(await support.DetailAsync(task, after, ct));
    }
}

/// <summary>
/// 復原主管重派：憑證解得開、未過期、是本人、同一個任務、目前負責人仍是當時派去的人、任務版本沒變，才改回原負責人
/// （照一般改負責人處理：系統記錄與通知照寫，已發的通知不收回）。任一不符 400，不改任何值。
/// </summary>
public class UndoReassignHandler(ITaskRepository tasks, TaskSupport support, TaskWriter writer, TaskAccess access, ITaskUndoTokens undoTokens,
    ICurrentUser user, IBusinessTime time)
    : IRequestHandler<UndoReassignRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UndoReassignRequest request, CancellationToken ct)
    {
        var invalid = ServiceResult.Invalid("undoToken", MessageRef.Literal(TaskMessages.UndoInvalid));
        var ticket = string.IsNullOrEmpty(request.Token) ? null : undoTokens.Read(request.Token);
        if (ticket is null || ticket.ActorUserId != user.UserId || ticket.TaskId != request.Id || ticket.Expires < time.Now) return invalid;
        var task = await tasks.GetAsync(request.Id, ct);
        if (task is null) return ServiceResult.NotFound(TaskMessages.NotFound);
        if ((await access.EvaluateAsync(task, ct)).Frozen) return ServiceResult.Conflict(ModuleGate.ClosedMessage);
        if (task.AssigneeUserId != ticket.ToUserId || TaskVersion.Of(task) != ticket.Version) return invalid;
        if (ticket.FromUserId is { } from && !await support.IsActiveUserAsync(from, ct)) return invalid;

        // 只改負責人，不會被連動規則擋下
        await writer.ApplyAsync(task, new TaskEdit(null, false, null, true, ticket.FromUserId, false, null, null), ct);
        await writer.FlushAsync(ct);
        var p = await access.EvaluateAsync(task, ct);
        return ServiceResult.Success(p.CanSee
            ? await support.DetailAsync(task, p, ct)
            : new TaskHiddenAfterWrite(task.Id, false, null));
    }
}

/// <summary>
/// 留言、參與者、附件資料列隨任務 cascade 刪除（實體檔交給清掃）；指向這筆任務的通知一起刪。父任務的子任務沒有 FK（ST-T7），
/// 在同一交易裡先刪（含別人建的、已完成的），通知一起清；刪的是子任務時在父任務留言串記 subtaskDeleted。
/// </summary>
public class DeleteTaskHandler(ITaskRepository tasks, INotificationRepository notifications, TaskAccess access, TaskWriter writer, IUnitOfWork uow)
    : IRequestHandler<DeleteTaskRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteTaskRequest request, CancellationToken ct)
    {
        var task = await tasks.GetAsync(request.Id, ct);
        if (task is null) return ServiceResult.NotFound(TaskMessages.NotFound);
        var p = await access.EvaluateAsync(task, ct);
        if (!p.CanSee) return ServiceResult.NotFound(TaskMessages.NotFound);
        if (p.Frozen) return ServiceResult.Conflict(ModuleGate.ClosedMessage);
        if (task.RuleId is not null) return ServiceResult.Conflict(TaskMessages.RuleTaskNoDelete);
        if (!p.CanDelete) return ServiceResult.Forbidden();
        const string source = NotificationService.TaskNotificationWriter.SourceType;
        if (task.ParentTaskId is null)
            foreach (var child in await tasks.ListChildrenAsync(task.Id, ct))
            {
                await notifications.RemoveBySourceAsync(source, child.Id, ct);
                tasks.Remove(child);
            }
        else if (await tasks.GetAsync(task.ParentTaskId.Value, ct) is { } parent)
            writer.AddSubtaskEntry(parent, TaskSystemEvents.SubtaskDeleted, task);
        await notifications.RemoveBySourceAsync(source, task.Id, ct);
        tasks.Remove(task);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}

public class GetMyTasksHandler(ITaskRepository tasks, TaskAccess access, IPermissionRepository permissions, ICurrentUser user, IBusinessTime time, IPolicyReader policies,
    TaskVisibilityService visibility, TaskSupport support)
    : IRequestHandler<GetMyTasksRequest, ServiceResult>
{
    private static readonly Dictionary<string, TaskListTab> Tabs = new()
    {
        ["assigned"] = TaskListTab.Assigned, ["created"] = TaskListTab.Created, ["mentioned"] = TaskListTab.Mentioned,
        ["subordinates"] = TaskListTab.Subordinates
    };

    private static readonly Dictionary<string, TaskListFilter> Filters = new()
    {
        ["open"] = TaskListFilter.Open, ["overdue"] = TaskListFilter.Overdue, ["today"] = TaskListFilter.Today, ["thisWeek"] = TaskListFilter.ThisWeek, ["all"] = TaskListFilter.All
    };

    public async Task<ServiceResult> Handle(GetMyTasksRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Tabs.TryGetValue(request.Tab, out var tab)) errors["tab"] = ["分頁不正確"];
        if (!Filters.TryGetValue(request.Filter, out var filter)) errors["filter"] = ["篩選不正確"];
        if (errors.Count > 0) return ServiceResult.Invalid(errors);
        var page = Math.Max(request.CurrentPage, 1);
        var size = Math.Clamp(request.PagesSize, 1, 100);
        var today = time.Today;
        var weekStart = PolicyCatalog.WeekStartOf(await policies.ChoiceAsync(PolicyCatalog.CalendarWeekStart, null, ct));
        // 政策關閉或沒有部屬時名單是空的，分頁回空清單（不回 400）
        var subordinates = tab == TaskListTab.Subordinates ? await visibility.SubordinatesAsync(ct) : null;
        var (items, total) = await tasks.QueryMineAsync(
            new TaskListQuery(user.UserId, tab, filter, today, TaskDates.WeekEnd(today, weekStart), page, size, subordinates), ct);
        var perms = await access.EvaluateManyAsync(items, ct);
        var users = (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId);
        var progress = await support.ProgressAsync(items, ct);
        // 縱深防禦：分頁條件靠參與者表維持「抓到的都看得到」，這裡再濾一次；濾掉的也從總數扣掉，不洩漏筆數
        var rows = items.Select((t, i) => (t, p: perms[i])).Where(x => x.p.CanSee)
            .Select(x => TaskViews.ToSummary(x.t, x.p, users, today, progress.GetValueOrDefault(x.t.Id))).ToList();
        return ServiceResult.Success(new PagedResponse<TaskSummaryResponse>(rows, total - (items.Count - rows.Count), page, size));
    }
}

public class GetTaskCapabilitiesHandler(TaskVisibilityService visibility) : IRequestHandler<GetTaskCapabilitiesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetTaskCapabilitiesRequest request, CancellationToken ct) =>
        ServiceResult.Success(new TaskCapabilitiesResponse((await visibility.SubordinatesAsync(ct)).Count > 0));
}

public class GetTaskSummaryHandler(ITaskRepository tasks, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<GetTaskSummaryRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetTaskSummaryRequest request, CancellationToken ct)
    {
        var (dueToday, overdue) = await tasks.CountMineDueAsync(user.UserId, time.Today, ct);
        return ServiceResult.Success(new TaskSummaryCounts(dueToday, overdue));
    }
}

public class GetTaskHandler(ITaskRepository tasks, TaskAccess access, TaskSupport support)
    : IRequestHandler<GetTaskRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetTaskRequest request, CancellationToken ct)
    {
        var task = await tasks.GetAsync(request.Id, ct);
        if (task is null) return ServiceResult.NotFound(TaskMessages.NotFound);
        var p = await access.EvaluateAsync(task, ct);
        // 看不到與不存在同一個回應，不洩漏任務存在
        if (!p.CanSee) return ServiceResult.NotFound(TaskMessages.NotFound);
        return ServiceResult.Success(await support.DetailAsync(task, p, ct));
    }
}

public class GetRecordTasksHandler(TaskAccess access, TaskSupport support)
    : IRequestHandler<GetRecordTasksRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetRecordTasksRequest request, CancellationToken ct)
    {
        var target = await access.ForRecordAsync(request.RecordId, ct);
        if (target is null) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        return ServiceResult.Success(await support.RecordTasksAsync(target, ct));
    }
}
