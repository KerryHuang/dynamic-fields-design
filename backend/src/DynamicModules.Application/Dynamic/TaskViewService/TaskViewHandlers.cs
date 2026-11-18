using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Tasks;
using MediatR;

namespace DynamicModules.Application.Dynamic.TaskViewService;

/// <summary>任務檢視（看板、甘特圖）存檔的輸入檢查。名稱長度、分享數量是技術限制（可寫死清單）。</summary>
public static class TaskViewRules
{
    public const int MaxName = 100;
    public const int MaxShares = 200;

    public static ServiceResult? KindError(string? kind) =>
        kind is TaskViewKinds.Board or TaskViewKinds.Gantt ? null : ServiceResult.Invalid("kind", MessageRef.Of(MessageText.TaskViewKindInvalid));

    public static (string? Name, ServiceResult? Error) Name(string? name, string kind = TaskViewKinds.Board)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return (null, ServiceResult.Invalid("name", TaskViewMessages.NameRequiredOf(kind)));
        if (trimmed.Length > MaxName) return (null, ServiceResult.Invalid("name", TaskViewMessages.NameTooLongOf(kind, MaxName)));
        return (trimmed, null);
    }

    /// <summary>看板設定：沒給時用 fallback（新增時是預設看板）；給了就嚴格驗證，錯了回 400（鍵 config）。</summary>
    public static (TaskBoardConfig? Config, ServiceResult? Error) Config(JsonElement? json, TaskBoardConfig? fallback)
    {
        if (json is not { } el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return fallback is null ? (null, Bad("config", "看板設定必填")) : (fallback, null);
        if (el.ValueKind != JsonValueKind.Object) return (null, Bad("config", "看板設定必須是物件"));
        var (config, errors) = TaskBoardConfig.Parse(el.GetRawText());
        return config is null ? (null, ServiceResult.Invalid(new Dictionary<string, string[]> { ["config"] = errors.ToArray() })) : (config, null);
    }

    /// <summary>甘特設定：規則同 <see cref="Config"/>，錯誤文字走 MessageText（GT-T8）。</summary>
    public static (TaskGanttConfig? Config, ServiceResult? Error) GanttConfig(JsonElement? json, TaskGanttConfig fallback)
    {
        if (json is not { } el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return (fallback, null);
        if (el.ValueKind != JsonValueKind.Object) return (null, ServiceResult.Invalid("config", MessageRef.Of(MessageText.TaskGanttConfigNotObject)));
        var (config, errors) = TaskGanttConfig.Parse(el.GetRawText());
        return config is null
            ? (null, ServiceResult.Invalid(new Dictionary<string, MessageRef[]> { ["config"] = errors.Select(e => MessageRef.Of(e.Key, e.Args)).ToArray() }))
            : (config, null);
    }

    /// <summary>依種類解析要存的設定；沒給設定時存 current（修改時是現有設定，新增時 null＝該種的預設）。</summary>
    public static (string? ConfigJson, ServiceResult? Error) ConfigJson(string kind, JsonElement? json, string? current)
    {
        if (kind == TaskViewKinds.Gantt)
        {
            var (gantt, ganttError) = GanttConfig(json, current is null ? TaskGanttConfig.Default : TaskGanttConfig.Read(current));
            return (gantt?.Serialize(), ganttError);
        }
        var (board, boardError) = Config(json, current is null ? TaskBoardConfig.Default : TaskBoardConfig.Read(current));
        return (board?.Serialize(), boardError);
    }

    /// <summary>依資料列的種類寬鬆讀取、正規化後的設定 JSON（詳細、另存用）。</summary>
    public static string ReadConfig(TaskView v) =>
        v.Kind == TaskViewKinds.Gantt ? TaskGanttConfig.Read(v.ConfigJson).Serialize() : TaskBoardConfig.Read(v.ConfigJson).Serialize();

    /// <summary>名稱加「（副本）」；超過上限時截短原名稱，保留後綴（不切斷代理字元對）。</summary>
    public static string CopyName(string name)
    {
        var suffix = TaskViewMessages.CopySuffix;
        if (name.Length + suffix.Length <= MaxName) return name + suffix;
        var cut = MaxName - suffix.Length;
        if (char.IsHighSurrogate(name[cut - 1])) cut--;
        return name[..cut] + suffix;
    }

    public static ServiceResult Bad(string key, string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { [key] = [message] });
}

public class GetTaskViewsHandler(ITaskViewRepository views, TaskViewAccess access, IPermissionRepository permissions)
    : IRequestHandler<GetTaskViewsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetTaskViewsRequest request, CancellationToken ct)
    {
        if (TaskViewRules.KindError(request.Kind) is { } bad) return bad;
        var viewer = await access.ViewerAsync(ct);
        var list = await views.ListVisibleAsync(request.Kind!, viewer.UserId, viewer.GroupIds, viewer.DepartmentId, viewer.IsAdmin, ct);
        var users = (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId);
        var items = list.Select(v => TaskViewAccess.Summary(v, viewer, users)).ToList();
        return ServiceResult.Success(new TaskViewListResponse(items.Where(v => v.IsOwner).ToList(), items.Where(v => !v.IsOwner).ToList()));
    }
}

public class CreateTaskViewHandler(ITaskViewRepository views, TaskViewAccess access, IUnitOfWork uow, IBusinessTime time)
    : IRequestHandler<CreateTaskViewRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateTaskViewRequest request, CancellationToken ct)
    {
        if (TaskViewRules.KindError(request.Kind) is { } badKind) return badKind;
        var kind = request.Kind!;
        var (name, badName) = TaskViewRules.Name(request.Name, kind);
        if (badName is not null) return badName;
        var (config, badConfig) = TaskViewRules.ConfigJson(kind, request.Config, null);
        if (badConfig is not null) return badConfig;

        var viewer = await access.ViewerAsync(ct);
        var view = new TaskView
        {
            Kind = kind, OwnerUserId = viewer.UserId, Name = name!, ConfigJson = config!,
            RowVersion = Guid.NewGuid().ToString()
        };
        view.StampCreated(viewer.UserId, time.Now);
        views.Add(view);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(await access.DetailAsync(view, viewer, ct));
    }
}

public class GetTaskViewHandler(TaskViewAccess access) : IRequestHandler<GetTaskViewRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetTaskViewRequest request, CancellationToken ct)
    {
        var (view, viewer) = await access.FindAsync(request.Id, ct);
        return view is null ? ServiceResult.NotFound(TaskViewMessages.NotFound) : ServiceResult.Success(await access.DetailAsync(view, viewer, ct));
    }
}

/// <summary>只有建立者能改（系統管理員也不能改別人的，§6.2）；不能改種類。</summary>
public class UpdateTaskViewHandler(TaskViewAccess access, IUnitOfWork uow, IBusinessTime time) : IRequestHandler<UpdateTaskViewRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateTaskViewRequest request, CancellationToken ct)
    {
        var (view, viewer) = await access.FindAsync(request.Id, ct);
        if (view is null) return ServiceResult.NotFound(TaskViewMessages.NotFound);
        if (view.OwnerUserId != viewer.UserId) return ServiceResult.Forbidden();
        if (view.RowVersion != request.RowVersion) return ServiceResult.Stale();
        // 種類不能改：依資料列的 Kind 驗證設定（GT-T9）
        var (name, badName) = TaskViewRules.Name(request.Name, view.Kind);
        if (badName is not null) return badName;
        var (config, badConfig) = TaskViewRules.ConfigJson(view.Kind, request.Config, view.ConfigJson);
        if (badConfig is not null) return badConfig;

        view.Name = name!;
        view.ConfigJson = config!;
        view.RowVersion = Guid.NewGuid().ToString();
        view.StampModified(viewer.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(await access.DetailAsync(view, viewer, ct));
    }
}

/// <summary>建立者，或系統管理員刪有分享的看板（避免離職者留下的共用看板沒人能管）；分享隨看板 cascade 刪除。</summary>
public class DeleteTaskViewHandler(ITaskViewRepository views, TaskViewAccess access, IUnitOfWork uow) : IRequestHandler<DeleteTaskViewRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteTaskViewRequest request, CancellationToken ct)
    {
        var (view, viewer) = await access.FindAsync(request.Id, ct);
        if (view is null) return ServiceResult.NotFound(TaskViewMessages.NotFound);
        if (!TaskViewAccess.CanDelete(view, viewer)) return ServiceResult.Forbidden();
        views.Remove(view);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}

public class CopyTaskViewHandler(ITaskViewRepository views, TaskViewAccess access, IUnitOfWork uow, IBusinessTime time)
    : IRequestHandler<CopyTaskViewRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CopyTaskViewRequest request, CancellationToken ct)
    {
        var (source, viewer) = await access.FindAsync(request.Id, ct);
        if (source is null) return ServiceResult.NotFound(TaskViewMessages.NotFound);
        var copy = new TaskView
        {
            Kind = source.Kind, OwnerUserId = viewer.UserId, Name = TaskViewRules.CopyName(source.Name),
            ConfigJson = TaskViewRules.ReadConfig(source), RowVersion = Guid.NewGuid().ToString()
        };
        copy.StampCreated(viewer.UserId, time.Now);
        views.Add(copy);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(await access.DetailAsync(copy, viewer, ct));
    }
}

/// <summary>整批取代分享；對象必須存在（停用的使用者、部門可以留著），最多 200 個。</summary>
public class SetTaskViewSharesHandler(TaskViewAccess access, IPermissionRepository permissions, IOrganizationRepository org,
    IUnitOfWork uow, IBusinessTime time) : IRequestHandler<SetTaskViewSharesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SetTaskViewSharesRequest request, CancellationToken ct)
    {
        var (view, viewer) = await access.FindAsync(request.Id, ct);
        if (view is null) return ServiceResult.NotFound(TaskViewMessages.NotFound);
        if (view.OwnerUserId != viewer.UserId) return ServiceResult.Forbidden();

        // JSON 陣列可以含 null 元素，不能直接解參照
        if (request.Shares.Any(s => s is null)) return TaskViewRules.Bad("shares", TaskViewMessages.ShareKindInvalid);
        var targets = request.Shares.Select(s => (Kind: s.Kind?.Trim() ?? "", Id: s.Id?.Trim() ?? "")).Distinct().ToList();
        if (targets.Count > TaskViewRules.MaxShares) return ServiceResult.Invalid("shares", TaskViewMessages.TooManySharesOf(view.Kind, TaskViewRules.MaxShares));
        if (targets.Any(t => t.Kind is not (TaskViewShareKinds.User or TaskViewShareKinds.Group or TaskViewShareKinds.Department) || t.Id.Length == 0))
            return TaskViewRules.Bad("shares", TaskViewMessages.ShareKindInvalid);
        var users = (await permissions.ListUsersAsync(ct)).Select(u => u.UserId).ToHashSet();
        var groups = (await permissions.ListGroupsAsync(ct)).Select(g => g.Id.ToString(CultureInfo.InvariantCulture)).ToHashSet();
        var departments = (await org.ListAsync(ct)).Select(d => d.Id.ToString(CultureInfo.InvariantCulture)).ToHashSet();
        bool Exists((string Kind, string Id) t) => t.Kind switch
        {
            TaskViewShareKinds.User => users.Contains(t.Id),
            TaskViewShareKinds.Group => groups.Contains(t.Id),
            _ => departments.Contains(t.Id),
        };
        if (!targets.All(Exists)) return TaskViewRules.Bad("shares", TaskViewMessages.ShareTargetMissing);

        // 只動差異：同一個 key 先刪再加會讓追蹤中的實體衝突
        view.Shares.RemoveAll(s => !targets.Contains((s.TargetKind, s.TargetId)));
        foreach (var (kind, id) in targets.Where(t => !view.Shares.Any(s => s.TargetKind == t.Kind && s.TargetId == t.Id)))
            view.Shares.Add(new TaskViewShare { ViewId = view.Id, TargetKind = kind, TargetId = id });
        view.RowVersion = Guid.NewGuid().ToString();
        view.StampModified(viewer.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(await access.DetailAsync(view, viewer, ct));
    }
}

public class GetTaskViewShareTargetsHandler(IPermissionRepository permissions, IOrganizationRepository org)
    : IRequestHandler<GetTaskViewShareTargetsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetTaskViewShareTargetsRequest request, CancellationToken ct)
    {
        var users = (await permissions.ListUsersAsync(ct)).Where(u => u.IsActive).OrderBy(u => u.UserId)
            .Select(u => new TaskViewUserTarget(u.UserId, u.DisplayName)).ToList();
        var departments = (await org.ListAsync(ct)).Where(d => d.Active).Select(d => new TaskViewNamedTarget(d.Id, d.NameI18n)).ToList();
        var groups = (await permissions.ListGroupsAsync(ct)).OrderBy(g => g.Id).Select(g => new TaskViewNamedTarget(g.Id, g.NameI18n)).ToList();
        return ServiceResult.Success(new TaskViewShareTargets(users, departments, groups));
    }
}
