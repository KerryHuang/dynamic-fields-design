using System.Text.Json;
using DynamicModules.Application.Dynamic.TaskService;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>看板欄查詢與移動、甘特查詢與改日期（看板設計 §12）。日期由伺服器算，不收瀏覽器的「今天」（T8）。</summary>
[ApiController]
[Route("api/v3/dynamic")]
public class TaskBoardController(IMediator mediator) : ControllerBase
{
    /// <summary>viewId 與 config 二擇一；都沒帶是預設看板。</summary>
    public sealed record ColumnsBody(long? ViewId, JsonElement? Config);

    public sealed record PageBody(long? ViewId, JsonElement? Config, int Offset);

    /// <summary>Cascade（complete、cancel）只在依狀態分欄時有作用：父任務拖到完成、還有未完成的子任務時一併完成或取消。</summary>
    public sealed record MoveBody(string? GroupBy, string? ToColumn, DateOnly? DueDate, string? ExpectedVersion, string? Cascade = null);

    /// <summary>兩個日期一律都送（null 代表清除），格式錯由模型繫結回 400。</summary>
    public sealed record ScheduleBody(DateOnly? StartDate, DateOnly? DueDate, string? ExpectedVersion);

    /// <summary>viewId 與 config 二擇一；都沒帶是預設甘特圖。window 選填（含頭含尾，最多 366 天）。</summary>
    public sealed record GanttBody(long? ViewId, JsonElement? Config, TaskGanttWindowInput? Window);

    [HttpPost("task-board/columns")]
    public async Task<IActionResult> Columns(ColumnsBody body) =>
        (await mediator.Send(new GetTaskBoardColumnsRequest(body.ViewId, body.Config))).ToActionResult(this);

    /// <summary>column 是欄鍵：todo、overdue、unassigned、user:&lt;帳號&gt; 等。</summary>
    [HttpPost("task-board/columns/{column}/page")]
    public async Task<IActionResult> Page(string column, PageBody body) =>
        (await mediator.Send(new GetTaskBoardColumnPageRequest(column, body.ViewId, body.Config, body.Offset))).ToActionResult(this);

    [HttpPost("tasks/{id:long}/move")]
    public async Task<IActionResult> Move(long id, MoveBody body) =>
        (await mediator.Send(new MoveTaskRequest(id, body.GroupBy, body.ToColumn, body.DueDate, body.ExpectedVersion, body.Cascade))).ToActionResult(this);

    /// <summary>甘特查詢（看板設計 §11.5.2.5）：時間軸列、未排程、父任務標題與子任務跨度。</summary>
    [HttpPost("tasks/gantt")]
    public async Task<IActionResult> Gantt(GanttBody body) =>
        (await mediator.Send(new GetTaskGanttRequest(body.ViewId, body.Config, body.Window))).ToActionResult(this);

    /// <summary>甘特圖改日期（看板設計 §11.5.2.6）。</summary>
    [HttpPost("tasks/{id:long}/schedule")]
    public async Task<IActionResult> Schedule(long id, ScheduleBody body) =>
        (await mediator.Send(new ScheduleTaskRequest(id, body.StartDate, body.DueDate, body.ExpectedVersion))).ToActionResult(this);
}
