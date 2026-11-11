using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
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

    public async Task<TaskDetailResponse> DetailAsync(TaskItem task, TaskPermissions p, CancellationToken ct)
    {
        var users = new Dictionary<string, AppUser>();
        foreach (var id in new[] { task.AssigneeUserId, task.CreatedBy }.OfType<string>().Distinct())
            if (await permissions.GetUserAsync(id, ct) is { } u) users[id] = u;
        var ruleKey = task.RuleId is { } ruleId ? await tasks.GetRuleKeyAsync(ruleId, ct) : null;
        return TaskViews.ToDetail(task, p, users, time.Today, ruleKey);
    }

    /// <summary>記錄上看得到的任務，加上可手動套用的規則。</summary>
    public async Task<RecordTasksResponse> RecordTasksAsync(RecordTaskAccess target, CancellationToken ct)
    {
        var items = await tasks.ListByRecordAsync(target.Record.Id, ct);
        var perms = await access.EvaluateManyAsync(items, ct);
        var users = (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId);
        var today = time.Today;
        var rows = items.Select((t, i) => (t, p: perms[i])).Where(x => x.p.CanSee)
            .Select(x => TaskViews.ToSummary(x.t, x.p, users, today)).ToList();
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

public class UpdateTaskHandler(ITaskRepository tasks, TaskSupport support, TaskWriter writer, TaskAccess access)
    : IRequestHandler<UpdateTaskRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateTaskRequest request, CancellationToken ct)
    {
        var task = await tasks.GetAsync(request.Id, ct);
        if (task is null) return ServiceResult.NotFound(TaskMessages.NotFound);
        var p = await access.EvaluateAsync(task, ct);
        if (!p.CanSee) return ServiceResult.NotFound(TaskMessages.NotFound);
        if (p.Frozen) return ServiceResult.Conflict(ModuleGate.ClosedMessage);
        if (!p.CanEdit) return ServiceResult.Forbidden();
        var (edit, errors) = TaskEditParser.Parse(request.Patch);
        if (edit is null) return ServiceResult.Invalid(errors);
        if (edit is { SetAssignee: true, AssigneeUserId: { } assignee } && assignee != task.AssigneeUserId
            && !await support.IsActiveUserAsync(assignee, ct))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["assigneeUserId"] = [TaskMessages.AssigneeInvalid] });
        await writer.ApplyAsync(task, edit, ct);
        await writer.FlushAsync(ct);
        return ServiceResult.Success(await support.DetailAsync(task, await access.EvaluateAsync(task, ct), ct));
    }
}

/// <summary>留言、參與者、附件資料列隨任務 cascade 刪除（實體檔交給清掃）；指向這筆任務的通知一起刪。</summary>
public class DeleteTaskHandler(ITaskRepository tasks, INotificationRepository notifications, TaskAccess access, IUnitOfWork uow)
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
        await notifications.RemoveBySourceAsync(NotificationService.TaskNotificationWriter.SourceType, task.Id, ct);
        tasks.Remove(task);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}

public class GetMyTasksHandler(ITaskRepository tasks, TaskAccess access, IPermissionRepository permissions, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<GetMyTasksRequest, ServiceResult>
{
    private static readonly Dictionary<string, TaskListTab> Tabs = new()
    {
        ["assigned"] = TaskListTab.Assigned, ["created"] = TaskListTab.Created, ["mentioned"] = TaskListTab.Mentioned
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
        var (items, total) = await tasks.QueryMineAsync(
            new TaskListQuery(user.UserId, tab, filter, today, WeekEnd(today), page, size), ct);
        var perms = await access.EvaluateManyAsync(items, ct);
        var users = (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId);
        // 縱深防禦：分頁條件靠參與者表維持「抓到的都看得到」，這裡再濾一次；濾掉的也從總數扣掉，不洩漏筆數
        var rows = items.Select((t, i) => (t, p: perms[i])).Where(x => x.p.CanSee)
            .Select(x => TaskViews.ToSummary(x.t, x.p, users, today)).ToList();
        return ServiceResult.Success(new PagedResponse<TaskSummaryResponse>(rows, total - (items.Count - rows.Count), page, size));
    }

    /// <summary>本週日（週一到週日為一週）。</summary>
    internal static DateOnly WeekEnd(DateOnly today) =>
        today.AddDays((7 - (int)today.DayOfWeek) % 7);
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
