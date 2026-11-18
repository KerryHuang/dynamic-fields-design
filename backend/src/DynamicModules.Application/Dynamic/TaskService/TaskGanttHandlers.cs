using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.TaskViewService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Tasks;
using MediatR;

namespace DynamicModules.Application.Dynamic.TaskService;

/// <summary>
/// 甘特查詢（看板設計 §11.5.2.5）：範圍、篩選、可見條件全部重用看板（甘特設定轉成同範圍、同篩選的看板設定交給
/// <see cref="TaskBoardService.ContextAsync"/>，GT-T16），卡片走 <see cref="TaskBoardService.CardsAsync"/>（一次批次評估，GT-T11）。
/// 時間軸 500 列、未排程 100 筆、時間窗 366 天是技術上限（可寫死清單 §14，GT-T13）。查詢次數不隨列數成長。
/// </summary>
public class GetTaskGanttHandler(ITaskRepository tasks, TaskBoardService board, TaskViewAccess views) : IRequestHandler<GetTaskGanttRequest, ServiceResult>
{
    public const int MaxRows = 500;
    public const int MaxUnscheduled = 100;
    public const int MaxWindowDays = 366;

    public async Task<ServiceResult> Handle(GetTaskGanttRequest request, CancellationToken ct)
    {
        var (config, error) = await ResolveAsync(request.ViewId, request.Config, ct);
        if (config is null) return error!;
        if (WindowError(request.Window) is { } badWindow) return badWindow;
        var (_, q, scopeAvailable) = await board.ContextAsync(AsBoard(config), ct);
        var weekStart = q.WeekEnd.AddDays(-6);
        var window = request.Window is { From: { } from, To: { } to } ? new TaskGanttWindow(from, to) : DefaultWindow(config.Scale, q.Today, weekStart);

        var (rowItems, total) = await tasks.QueryGanttAsync(q, window.From, window.To, MaxRows, ct);
        var (unscheduledItems, unscheduledTotal) = await tasks.QueryUnscheduledAsync(q, MaxUnscheduled, ct);

        // 依父子分組：時間軸列裡的子任務、父任務不在列裡時，補父任務（看得到的再看是否符合範圍與篩選）
        var byParent = config.GroupBy == TaskGanttConfig.ByParent;
        var rowIds = rowItems.Select(t => t.Id).ToHashSet();
        var missing = byParent
            ? rowItems.Where(t => t.ParentTaskId is { } p && !rowIds.Contains(p)).Select(t => t.ParentTaskId!.Value).Distinct().ToList()
            : [];
        var parentItems = missing.Count == 0 ? [] : await tasks.ListByIdsAsync(missing, ct);
        var matching = missing.Count == 0 ? [] : await tasks.ListMatchingIdsAsync(q, missing, ct);

        // 列、未排程、父任務一起批次評估；看不到的父任務不會有卡片（只用來判斷 visible，卡片不回）
        var cards = await board.CardsAsync(rowItems.Concat(unscheduledItems).Concat(parentItems).DistinctBy(t => t.Id).ToList(),
            q.Today, await board.UsersAsync(ct), ct);
        var rows = rowItems.Where(t => cards.ContainsKey(t.Id)).Select(t => cards[t.Id]).ToList();
        var unscheduled = unscheduledItems.Where(t => cards.ContainsKey(t.Id)).Select(t => cards[t.Id]).ToList();

        var parents = new List<TaskGanttParent>();
        foreach (var id in missing)
        {
            var child = rows.FirstOrDefault(c => c.ParentTaskId == id);
            if (child is null) continue;
            var visible = cards.TryGetValue(id, out var parentCard);
            // 看不到的父任務只露標題（TaskPermissions.Parent，st1 U13），不多給
            var title = visible ? parentCard!.Title : child.ParentTitle;
            if (title is null) continue;
            parents.Add(new TaskGanttParent(id, title, visible, visible && matching.Contains(id) ? parentCard : null));
        }

        var spans = byParent ? await SpansAsync(rows, parents, ct) : [];
        return ServiceResult.Success(new TaskGanttResponse(q.Today, weekStart.DayOfWeek.ToString().ToLowerInvariant(), window, scopeAvailable,
            JsonDocument.Parse(config.Serialize()).RootElement.Clone(), rows, total, total > MaxRows, parents, spans,
            unscheduled, unscheduledTotal, unscheduledTotal > MaxUnscheduled));
    }

    /// <summary>
    /// 子任務跨度（GT-T15）：看得到的父任務用它全部未取消的子任務（資料庫一次 GROUP BY）；看不到的父任務只用這次回應列裡它的子任務，
    /// 不讀其他子任務的日期（避免從跨度推出看不到的子任務日期）。
    /// </summary>
    private async Task<List<TaskGanttSpan>> SpansAsync(IReadOnlyList<TaskCardResponse> rows, IReadOnlyList<TaskGanttParent> parents, CancellationToken ct)
    {
        var visibleIds = rows.Where(c => c.ParentTaskId is null && c.Progress is not null).Select(c => c.Id)
            .Concat(parents.Where(p => p.Visible).Select(p => p.Id)).ToList();
        var spans = (await tasks.SubtaskSpanAsync(visibleIds, ct)).Select(kv => new TaskGanttSpan(kv.Key, kv.Value.From, kv.Value.To)).ToList();
        foreach (var p in parents.Where(p => !p.Visible))
        {
            var dated = rows.Where(c => c.ParentTaskId == p.Id && c.Status != TaskItemStatus.Cancelled && (c.StartDate ?? c.DueDate) is not null).ToList();
            if (dated.Count == 0) continue;
            spans.Add(new TaskGanttSpan(p.Id, dated.Min(c => (c.StartDate ?? c.DueDate)!.Value), dated.Max(c => (c.DueDate ?? c.StartDate)!.Value)));
        }
        return spans.OrderBy(s => s.ParentId).ToList();
    }

    /// <summary>ViewId 要看得到、而且是甘特圖（否則 404，看板 Id 也是）；Config 嚴格驗證（400）；兩者都帶 400；都沒帶是預設甘特圖。</summary>
    private async Task<(TaskGanttConfig? Config, ServiceResult? Error)> ResolveAsync(long? viewId, JsonElement? config, CancellationToken ct)
    {
        var hasConfig = config is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };
        if (viewId is not null && hasConfig) return (null, ServiceResult.Invalid("config", MessageRef.Of(MessageText.TaskGanttSourceBoth)));
        if (viewId is not { } id) return TaskViewRules.GanttConfig(config, TaskGanttConfig.Default);
        var (view, _) = await views.FindAsync(id, ct);
        return view is null || view.Kind != TaskViewKinds.Gantt
            ? (null, ServiceResult.NotFound(MessageRef.Of(MessageText.TaskGanttNotFound)))
            : (TaskGanttConfig.Read(view.ConfigJson), null);
    }

    private static ServiceResult? WindowError(TaskGanttWindowInput? w)
    {
        if (w is null) return null;
        if (w is not { From: { } from, To: { } to }) return ServiceResult.Invalid("window", MessageRef.Of(MessageText.TaskGanttWindowRequired));
        if (from > to) return ServiceResult.Invalid("window", MessageRef.Of(MessageText.TaskGanttWindowInvalid));
        return to.DayNumber - from.DayNumber + 1 > MaxWindowDays
            ? ServiceResult.Invalid("window", MessageRef.Of(MessageText.TaskGanttWindowTooLong, MaxWindowDays))
            : null;
    }

    /// <summary>沒帶時間窗（GT-T12）：日 14 天、週 13 週，從本週起始日起算；月 6 個月，從本月一日起算。</summary>
    public static TaskGanttWindow DefaultWindow(string scale, DateOnly today, DateOnly weekStart) => scale switch
    {
        TaskGanttConfig.ScaleDay => new(weekStart, weekStart.AddDays(13)),
        TaskGanttConfig.ScaleMonth => new(new DateOnly(today.Year, today.Month, 1), new DateOnly(today.Year, today.Month, 1).AddMonths(6).AddDays(-1)),
        _ => new(weekStart, weekStart.AddDays(13 * 7 - 1)),
    };

    /// <summary>甘特設定轉成同範圍、同篩選的看板設定（依狀態分欄，狀態篩選照設定；GroupBy、排序在甘特查詢裡不讀）。</summary>
    private static TaskBoardConfig AsBoard(TaskGanttConfig g) =>
        new(TaskBoardConfig.CurrentVersion, TaskBoardConfig.ByStatus, g.Scope, g.Filters is { } f ? f with { DueRange = null } : null, null, null, g.ShowSubtasks);
}
