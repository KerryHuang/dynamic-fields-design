using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.TaskService;

/// <summary>任務請求共用：輸入驗證與詳細回應組裝。</summary>
public sealed class TaskSupport(ITaskRepository tasks, IPermissionRepository permissions, IBusinessTime time)
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
}

public class CreateTaskHandler(TaskSupport support, TaskWriter writer, TaskAccess access, ICurrentUser user)
    : IRequestHandler<CreateTaskRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateTaskRequest request, CancellationToken ct)
    {
        var input = request.Input;
        if (await support.ValidateInputAsync(input, ct) is { Count: > 0 } errors) return ServiceResult.Invalid(errors);
        var task = writer.Create(new NewTask(input.Title.Trim(), TaskEditParser.NullIfBlank(input.Description),
            TaskEditParser.NullIfBlank(input.AssigneeUserId), input.DueDate, null, null, null, null, null, user.UserId));
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
        var input = request.Input;
        if (await support.ValidateInputAsync(input, ct) is { Count: > 0 } errors) return ServiceResult.Invalid(errors);
        var task = writer.Create(new NewTask(input.Title.Trim(), TaskEditParser.NullIfBlank(input.Description),
            TaskEditParser.NullIfBlank(input.AssigneeUserId), input.DueDate,
            target.Module.Id, target.Record.Id, target.Record.Title, null, null, user.UserId));
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

public class DeleteTaskHandler(ITaskRepository tasks, TaskAccess access, IUnitOfWork uow)
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
        tasks.Remove(task);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}
