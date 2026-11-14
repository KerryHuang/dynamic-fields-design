using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.TaskViewService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Tasks;
using MediatR;

namespace DynamicModules.Application.Dynamic.TaskService;

/// <summary>
/// 看板共用：解析看板設定（存檔或未存的）、伺服器算的日期、查詢條件、卡片組裝（看板設計 §5）。
/// 每欄每頁 50 張、每欄最多 500 張、負責人看板最多 50 位負責人的欄，都是技術限制（可寫死清單，T9、J5）。
/// </summary>
public sealed class TaskBoardService(ITaskRepository tasks, TaskAccess access, TaskVisibilityService visibility, TaskViewAccess views,
    IPermissionRepository permissions, IPolicyReader policies, IBusinessTime time)
{
    public const int PageSize = 50;
    public const int MaxCards = 500;
    public const int MaxAssigneeColumns = 50;

    public sealed record Context(TaskBoardConfig Config, TaskBoardQuery Query, bool ScopeAvailable);

    /// <summary>ViewId 要看得到（看不到 404）；Config 嚴格驗證（400）；兩者都帶 400；都沒帶是預設看板。</summary>
    public async Task<(TaskBoardConfig? Config, ServiceResult? Error)> ResolveAsync(long? viewId, JsonElement? config, CancellationToken ct)
    {
        var hasConfig = config is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };
        if (viewId is not null && hasConfig) return (null, TaskViewRules.Bad("config", TaskViewMessages.SourceBoth));
        if (viewId is { } id)
        {
            var (view, _) = await views.FindAsync(id, ct);
            return view is null ? (null, ServiceResult.NotFound(TaskViewMessages.NotFound)) : (TaskBoardConfig.Read(view.ConfigJson), null);
        }
        return TaskViewRules.Config(config, TaskBoardConfig.Default);
    }

    /// <summary>今天（營運時區）、本週最後一天（政策 calendar.weekStart）。</summary>
    public async Task<(DateOnly Today, DateOnly WeekEnd, DayOfWeek WeekStart)> DatesAsync(CancellationToken ct)
    {
        var today = time.Today;
        var weekStart = PolicyCatalog.WeekStartOf(await policies.ChoiceAsync(PolicyCatalog.CalendarWeekStart, null, ct));
        return (today, TaskDates.WeekEnd(today, weekStart), weekStart);
    }

    public async Task<Context> ContextAsync(TaskBoardConfig c, CancellationToken ct)
    {
        var (today, weekEnd, weekStart) = await DatesAsync(ct);
        var v = await visibility.ForCurrentUserAsync(ct);
        // 政策關閉或沒有部屬時是空集合：欄照列、全部 0 筆（D7）
        var subordinates = c.Scope == TaskBoardConfig.ScopeSubordinates ? await visibility.SubordinatesAsync(ct) : new HashSet<string>();
        var f = c.Filters;
        DateOnly? from = null, to = null;
        if (f?.DueRange is { } range) (from, to) = ReportDates.Resolve(range, today, weekStart);
        var query = new TaskBoardQuery(v, c.GroupBy, c.Scope, subordinates, c.StatusFilter(), f?.AssigneeUserIds, f?.IncludeUnassigned,
            f?.CreatorUserIds, f?.Sources, from, to, f?.Keyword?.Trim(), today, weekEnd, c.SortKey, c.SortDescending);
        return new Context(c, query, c.Scope != TaskBoardConfig.ScopeSubordinates || subordinates.Count > 0);
    }

    public static bool IsColumn(string groupBy, string column) => groupBy switch
    {
        TaskBoardConfig.ByStatus => TaskBoardConfig.StatusColumns.Contains(column),
        TaskBoardConfig.ByDueBucket => TaskDueBuckets.Keys.Contains(column),
        _ => column == TaskBoardConfig.Unassigned
            || (column.StartsWith(TaskBoardConfig.UserColumnPrefix, StringComparison.Ordinal) && column.Length > TaskBoardConfig.UserColumnPrefix.Length),
    };

    public async Task<Dictionary<string, AppUser>> UsersAsync(CancellationToken ct) =>
        (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId);

    /// <summary>一次評估所有卡片的權限與記錄遮蔽（kb1 的批次評估）；縱深防禦：看不到的不回。</summary>
    public async Task<Dictionary<long, TaskCardResponse>> CardsAsync(IReadOnlyList<TaskItem> items, DateOnly today,
        IReadOnlyDictionary<string, AppUser> users, CancellationToken ct)
    {
        if (items.Count == 0) return [];
        var perms = await access.EvaluateManyAsync(items, ct);
        var comments = await tasks.CountCommentsAsync(items.Select(t => t.Id).ToList(), ct);
        return items.Select((t, i) => (t, p: perms[i])).Where(x => x.p.CanSee)
            .ToDictionary(x => x.t.Id, x => Card(x.t, x.p, users, today, comments.GetValueOrDefault(x.t.Id)));
    }

    public static TaskCardResponse Card(TaskItem t, TaskPermissions p, IReadOnlyDictionary<string, AppUser> users, DateOnly today, int comments)
    {
        var assignee = t.AssigneeUserId is null ? null : users.GetValueOrDefault(t.AssigneeUserId);
        return new TaskCardResponse(t.Id, t.Title, t.Status, t.AssigneeUserId, assignee?.DisplayName, assignee is { IsActive: true },
            t.DueDate, TaskViews.Overdue(t, today), t.IsOpen && t.DueDate == today, p.Record, t.RuleId is not null, p.Frozen, comments,
            t.CompletedTime, TaskVersion.Of(t), p.CanEdit, p.CanReassign);
    }
}

public class GetTaskBoardColumnsHandler(ITaskRepository tasks, TaskBoardService board) : IRequestHandler<GetTaskBoardColumnsRequest, ServiceResult>
{
    private sealed record Spec(string Key, string Kind, string? UserId, string? Name, bool? Inactive, bool Droppable, string? DropReason);

    public async Task<ServiceResult> Handle(GetTaskBoardColumnsRequest request, CancellationToken ct)
    {
        var (config, error) = await board.ResolveAsync(request.ViewId, request.Config, ct);
        if (config is null) return error!;
        var (c, q, scopeAvailable) = await board.ContextAsync(config, ct);
        var users = await board.UsersAsync(ct);
        var omitted = 0;
        List<Spec> specs;
        Dictionary<string, int> counts;
        if (c.GroupBy == TaskBoardConfig.ByAssignee)
        {
            counts = await tasks.CountBoardColumnsAsync(q, [], ct);
            (specs, omitted) = AssigneeColumns(c, q, counts, users);
        }
        else
        {
            var keys = c.VisibleFixedColumns();
            counts = await tasks.CountBoardColumnsAsync(q, keys, ct);
            specs = keys.Select(k => FixedColumn(c.GroupBy, k, q)).ToList();
        }

        var pages = new Dictionary<string, List<TaskItem>>();
        foreach (var s in specs.Where(s => counts.GetValueOrDefault(s.Key) > 0))
            pages[s.Key] = await tasks.QueryBoardColumnAsync(q, s.Key, 0, TaskBoardService.PageSize, ct);
        var cards = await board.CardsAsync(pages.Values.SelectMany(p => p).ToList(), q.Today, users, ct);

        var columns = specs.Select(s =>
        {
            var total = counts.GetValueOrDefault(s.Key);
            var list = pages.GetValueOrDefault(s.Key, []).Where(t => cards.ContainsKey(t.Id)).Select(t => cards[t.Id]).ToList();
            return new TaskBoardColumnResponse(s.Key, s.Kind, s.UserId, s.Name, s.Inactive, total, list,
                Math.Min(total, TaskBoardService.MaxCards) > list.Count, total > TaskBoardService.MaxCards, s.Droppable, s.DropReason);
        }).ToList();
        return ServiceResult.Success(new TaskBoardResponse(q.Today, q.WeekEnd, TaskDueBuckets.LaterDefault(q.WeekEnd), scopeAvailable, columns, omitted));
    }

    private static Spec FixedColumn(string groupBy, string key, TaskBoardQuery q)
    {
        if (groupBy == TaskBoardConfig.ByStatus) return new Spec(key, "status", null, null, null, true, null);
        var block = TaskDueBuckets.DropBlock(key, q.Today, q.WeekEnd);
        var reason = block switch
        {
            DueTargetError.OverdueNotAllowed => TaskMessages.DropOverdue,
            DueTargetError.ThisWeekEmpty => TaskMessages.DropThisWeekEmpty,
            _ => null,
        };
        return new Spec(key, "dueBucket", null, null, null, reason is null, reason);
    }

    /// <summary>
    /// 負責人欄（看板設計 §5.4、T6）：明列的人（含停用，不能放）→「未指派」→ 範圍是部屬時的全部部屬 → 自動附加篩選結果裡有任務的其他人；
    /// 後兩組依任務數多到少、再依帳號。人的欄合計最多 50 欄，超過的算進 omitted。
    /// </summary>
    private static (List<Spec> Specs, int Omitted) AssigneeColumns(TaskBoardConfig c, TaskBoardQuery q, Dictionary<string, int> counts,
        IReadOnlyDictionary<string, AppUser> users)
    {
        var specs = new List<Spec>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var persons = 0;
        var omitted = 0;
        int Count(string id) => counts.GetValueOrDefault(TaskBoardConfig.UserColumnPrefix + id);
        void Add(string id)
        {
            if (!listed.Add(id)) return;
            if (persons >= TaskBoardService.MaxAssigneeColumns)
            {
                omitted++;
                return;
            }
            persons++;
            var u = users.GetValueOrDefault(id);
            var inactive = u is not { IsActive: true };
            specs.Add(new Spec(TaskBoardConfig.UserColumnPrefix + id, "user", id, u?.DisplayName, inactive, !inactive,
                inactive ? TaskMessages.DropInactiveAssignee : null));
        }

        foreach (var id in c.Columns?.Assignees ?? []) Add(id);
        if (!c.Hides(TaskBoardConfig.Unassigned)) specs.Add(new Spec(TaskBoardConfig.Unassigned, "unassigned", null, null, null, true, null));
        if (c.Scope == TaskBoardConfig.ScopeSubordinates)
            foreach (var id in q.Subordinates.OrderByDescending(Count).ThenBy(id => id, StringComparer.Ordinal)) Add(id);
        if (c.AutoAppendAssignees)
            foreach (var id in counts.Keys.Where(k => k.StartsWith(TaskBoardConfig.UserColumnPrefix, StringComparison.Ordinal))
                         .Select(k => k[TaskBoardConfig.UserColumnPrefix.Length..]).OrderByDescending(Count).ThenBy(id => id, StringComparer.Ordinal))
                Add(id);
        return (specs, omitted);
    }
}

public class GetTaskBoardColumnPageHandler(ITaskRepository tasks, TaskBoardService board) : IRequestHandler<GetTaskBoardColumnPageRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetTaskBoardColumnPageRequest request, CancellationToken ct)
    {
        var (config, error) = await board.ResolveAsync(request.ViewId, request.Config, ct);
        if (config is null) return error!;
        if (!TaskBoardService.IsColumn(config.GroupBy, request.Column)) return TaskViewRules.Bad("column", TaskMessages.BadColumn);
        var (_, q, _) = await board.ContextAsync(config, ct);
        var total = (await tasks.CountBoardColumnsAsync(q, [request.Column], ct)).GetValueOrDefault(request.Column);
        var truncated = total > TaskBoardService.MaxCards;
        var offset = Math.Max(request.Offset, 0);
        if (offset >= TaskBoardService.MaxCards) return ServiceResult.Success(new TaskBoardPageResponse(request.Column, total, [], false, truncated));
        var items = await tasks.QueryBoardColumnAsync(q, request.Column, offset, Math.Min(TaskBoardService.PageSize, TaskBoardService.MaxCards - offset), ct);
        var cards = await board.CardsAsync(items, q.Today, await board.UsersAsync(ct), ct);
        var list = items.Where(t => cards.ContainsKey(t.Id)).Select(t => cards[t.Id]).ToList();
        return ServiceResult.Success(new TaskBoardPageResponse(request.Column, total, list,
            offset + items.Count < Math.Min(total, TaskBoardService.MaxCards), truncated));
    }
}

/// <summary>
/// 看板移動（看板設計 §5.2–§5.6）：值一律走 <see cref="TaskWriter.ApplyAsync"/>（覆寫旗標、系統記錄、通知與 PATCH 相同，T7）；
/// 版本不符 409、看不到 404、凍結 409；狀態與到期要能編輯，負責人另外允許部屬任務的主管；同欄不改值。
/// 寫入後看不到了（主管派到範圍外）時不回卡片，只給復原憑證（kb1）。
/// </summary>
public class MoveTaskHandler(ITaskRepository tasks, TaskAccess access, TaskSupport support, TaskWriter writer, TaskBoardService board,
    ITaskUndoTokens undoTokens, ICurrentUser user, IBusinessTime time) : IRequestHandler<MoveTaskRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(MoveTaskRequest request, CancellationToken ct)
    {
        var task = await tasks.GetAsync(request.Id, ct);
        if (task is null) return ServiceResult.NotFound(TaskMessages.NotFound);
        var p = await access.EvaluateAsync(task, ct);
        if (!p.CanSee) return ServiceResult.NotFound(TaskMessages.NotFound);
        if (p.Frozen) return ServiceResult.Conflict(ModuleGate.ClosedMessage);
        if (string.IsNullOrEmpty(request.ExpectedVersion)) return TaskViewRules.Bad("expectedVersion", TaskMessages.ExpectedVersionRequired);
        if (request.ExpectedVersion != TaskVersion.Of(task)) return ServiceResult.Conflict(MessageRef.Literal(TaskMessages.Stale), "stale");
        var to = request.ToColumn ?? "";

        TaskEdit? edit;
        switch (request.GroupBy)
        {
            case TaskBoardConfig.ByStatus:
                if (!p.CanEdit) return ServiceResult.Forbidden();
                if (!TaskBoardConfig.TryParseStatus(to, out var status)) return TaskViewRules.Bad("toColumn", TaskMessages.BadColumn);
                edit = status == task.Status ? null : new TaskEdit(null, false, null, false, null, false, null, status);
                break;
            case TaskBoardConfig.ByDueBucket:
                if (!p.CanEdit) return ServiceResult.Forbidden();
                var (today, weekEnd, _) = await board.DatesAsync(ct);
                if (!TaskDueBuckets.Keys.Contains(to)) return TaskViewRules.Bad("toColumn", TaskMessages.BadColumn);
                if (TaskDueBuckets.Of(task.DueDate, today, weekEnd) == to)
                {
                    edit = null;
                    break;
                }
                var target = TaskDueBuckets.TargetDate(to, today, weekEnd, request.DueDate);
                if (!target.Ok) return DueError(target.Error);
                edit = new TaskEdit(null, false, null, false, null, true, target.Date, null);
                break;
            case TaskBoardConfig.ByAssignee:
                if (!p.CanEdit && !p.CanReassign) return ServiceResult.Forbidden();
                if (!TaskBoardService.IsColumn(TaskBoardConfig.ByAssignee, to)) return TaskViewRules.Bad("toColumn", TaskMessages.BadColumn);
                var assignee = to == TaskBoardConfig.Unassigned ? null : to[TaskBoardConfig.UserColumnPrefix.Length..];
                if (assignee == task.AssigneeUserId)
                {
                    edit = null;
                    break;
                }
                if (assignee is not null && !await support.IsActiveUserAsync(assignee, ct))
                    return TaskViewRules.Bad("toColumn", TaskMessages.AssigneeInvalid);
                edit = new TaskEdit(null, false, null, true, assignee, false, null, null);
                break;
            default:
                return TaskViewRules.Bad("groupBy", TaskMessages.BadGroupBy);
        }

        var from = task.AssigneeUserId;
        if (edit is not null)
        {
            // 版本比對與寫入要原子：另一個請求在上面的讀取後已改過這一列時，認領失敗，整筆交易回滾（沒有事件、通知、旗標）
            if (!await tasks.TryClaimVersionAsync(task, ct)) return ServiceResult.Conflict(MessageRef.Literal(TaskMessages.Stale), "stale");
            await writer.ApplyAsync(task, edit, ct);
            await writer.FlushAsync(ct);
        }
        var after = edit is null ? p : await access.EvaluateAsync(task, ct);
        if (!after.CanSee)
        {
            // 只有改負責人會讓人看不到；復原只把負責人改回去
            var token = task.AssigneeUserId != from
                ? undoTokens.Issue(new TaskUndoTicket(task.Id, user.UserId, from, task.AssigneeUserId, TaskVersion.Of(task), time.Now + UpdateTaskHandler.UndoWindow))
                : null;
            return ServiceResult.Success(new TaskMoveResponse(false, null, token));
        }
        var cards = await board.CardsAsync([task], time.Today, await board.UsersAsync(ct), ct);
        return ServiceResult.Success(new TaskMoveResponse(true, cards[task.Id], null));
    }

    private static ServiceResult DueError(DueTargetError e) => e switch
    {
        DueTargetError.OverdueNotAllowed => TaskViewRules.Bad("toColumn", TaskMessages.DropOverdue),
        DueTargetError.ThisWeekEmpty => TaskViewRules.Bad("toColumn", TaskMessages.DropThisWeekEmpty),
        DueTargetError.LaterNeedsDate => TaskViewRules.Bad("dueDate", TaskMessages.LaterNeedsDate),
        DueTargetError.LaterNotAfterWeekEnd => TaskViewRules.Bad("dueDate", TaskMessages.LaterNotAfterWeekEnd),
        _ => TaskViewRules.Bad("toColumn", TaskMessages.BadColumn),
    };
}
