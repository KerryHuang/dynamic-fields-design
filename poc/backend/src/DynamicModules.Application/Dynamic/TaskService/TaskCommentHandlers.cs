using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.TaskService;

/// <summary>留言串（留言與系統記錄同一條時間軸）。看得到任務就看得到全部留言。</summary>
public class GetTaskCommentsHandler(ITaskRepository tasks, TaskAccess access, IPermissionRepository permissions, ICurrentUser user)
    : IRequestHandler<GetTaskCommentsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetTaskCommentsRequest request, CancellationToken ct)
    {
        var task = await tasks.GetAsync(request.TaskId, ct);
        if (task is null) return ServiceResult.NotFound(TaskMessages.NotFound);
        var p = await access.EvaluateAsync(task, ct);
        if (!p.CanSee) return ServiceResult.NotFound(TaskMessages.NotFound);
        var users = (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId);
        var admin = await access.IsSystemAdminAsync(ct);
        var rows = (await tasks.ListCommentsAsync(task.Id, ct))
            .Select(c => TaskViews.ToComment(c, users, TaskCommentRules.CanWithdraw(c, p, admin, user.UserId))).ToList();
        return ServiceResult.Success(rows);
    }
}

public class AddTaskCommentHandler(ITaskRepository tasks, TaskAccess access, TaskWriter writer, IPermissionRepository permissions, ICurrentUser user)
    : IRequestHandler<AddTaskCommentRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AddTaskCommentRequest request, CancellationToken ct)
    {
        var task = await tasks.GetAsync(request.TaskId, ct);
        if (task is null) return ServiceResult.NotFound(TaskMessages.NotFound);
        var p = await access.EvaluateAsync(task, ct);
        if (!p.CanSee) return ServiceResult.NotFound(TaskMessages.NotFound);
        if (p.Frozen) return ServiceResult.Conflict(ModuleGate.ClosedMessage);

        var body = request.Body?.Trim() ?? "";
        if (body.Length == 0 && request.Files.Count == 0) return Invalid("body", TaskMessages.BodyRequired);
        if (body.Length > TaskCommentRules.BodyMax) return Invalid("body", TaskMessages.BodyTooLong);

        var users = (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId);
        var active = users.Values.Where(u => u.IsActive).Select(u => u.UserId).ToHashSet(StringComparer.Ordinal);
        var comment = await writer.AddCommentAsync(task, body, active, ct);
        await writer.FlushAsync(ct);
        var admin = await access.IsSystemAdminAsync(ct);
        return ServiceResult.Created(TaskViews.ToComment(comment, users, TaskCommentRules.CanWithdraw(comment, p, admin, user.UserId)));
    }

    private static ServiceResult Invalid(string key, string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { [key] = [message] });
}

/// <summary>順序：任務 404 → 凍結 409 → 留言 404 → 系統記錄 409 → 不是作者也不是系統管理員 403。已撤回的再撤回一次照樣成功。</summary>
public class WithdrawTaskCommentHandler(ITaskRepository tasks, TaskAccess access, TaskWriter writer, IPermissionRepository permissions, ICurrentUser user)
    : IRequestHandler<WithdrawTaskCommentRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(WithdrawTaskCommentRequest request, CancellationToken ct)
    {
        var task = await tasks.GetAsync(request.TaskId, ct);
        if (task is null) return ServiceResult.NotFound(TaskMessages.NotFound);
        var p = await access.EvaluateAsync(task, ct);
        if (!p.CanSee) return ServiceResult.NotFound(TaskMessages.NotFound);
        if (p.Frozen) return ServiceResult.Conflict(ModuleGate.ClosedMessage);
        var comment = await tasks.GetCommentAsync(task.Id, request.CommentId, ct);
        if (comment is null) return ServiceResult.NotFound(TaskMessages.CommentNotFound);
        if (comment.Kind == TaskCommentKind.System) return ServiceResult.Conflict(TaskMessages.SystemEntryNoWithdraw);
        var admin = await access.IsSystemAdminAsync(ct);
        if (comment.AuthorUserId != user.UserId && !admin) return ServiceResult.Forbidden();
        writer.Withdraw(comment);
        await writer.FlushAsync(ct);
        var users = (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId);
        return ServiceResult.Success(TaskViews.ToComment(comment, users, false));
    }
}

public class GetMentionCandidatesHandler(IPermissionRepository permissions)
    : IRequestHandler<GetMentionCandidatesRequest, ServiceResult>
{
    public const int Max = 20;

    public async Task<ServiceResult> Handle(GetMentionCandidatesRequest request, CancellationToken ct)
    {
        var keyword = request.Keyword?.Trim() ?? "";
        var rows = (await permissions.ListUsersAsync(ct))
            .Where(u => u.IsActive && (keyword.Length == 0
                || u.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || u.UserId.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(u => u.UserId, StringComparer.Ordinal).Take(Max)
            .Select(u => new UserResponse(u.UserId, u.DisplayName)).ToList();
        return ServiceResult.Success(rows);
    }
}

public static class TaskCommentRules
{
    public const int BodyMax = 4000;

    /// <summary>人寫的、未撤回、任務沒凍結，且是作者或系統管理員。</summary>
    public static bool CanWithdraw(TaskComment c, TaskPermissions p, bool admin, string me) =>
        c.Kind == TaskCommentKind.Comment && !c.Withdrawn && !p.Frozen && (admin || c.AuthorUserId == me);
}
