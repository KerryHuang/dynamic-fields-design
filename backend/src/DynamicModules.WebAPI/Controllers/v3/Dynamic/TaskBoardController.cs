using System.Text.Json;
using DynamicModules.Application.Dynamic.TaskService;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>看板欄查詢與移動（看板設計 §12）。日期由伺服器算，不收瀏覽器的「今天」（T8）。</summary>
[ApiController]
[Route("api/v3/dynamic")]
public class TaskBoardController(IMediator mediator) : ControllerBase
{
    /// <summary>viewId 與 config 二擇一；都沒帶是預設看板。</summary>
    public sealed record ColumnsBody(long? ViewId, JsonElement? Config);

    public sealed record PageBody(long? ViewId, JsonElement? Config, int Offset);

    public sealed record MoveBody(string? GroupBy, string? ToColumn, DateOnly? DueDate, string? ExpectedVersion);

    [HttpPost("task-board/columns")]
    public async Task<IActionResult> Columns(ColumnsBody body) =>
        (await mediator.Send(new GetTaskBoardColumnsRequest(body.ViewId, body.Config))).ToActionResult(this);

    /// <summary>column 是欄鍵：todo、overdue、unassigned、user:&lt;帳號&gt; 等。</summary>
    [HttpPost("task-board/columns/{column}/page")]
    public async Task<IActionResult> Page(string column, PageBody body) =>
        (await mediator.Send(new GetTaskBoardColumnPageRequest(column, body.ViewId, body.Config, body.Offset))).ToActionResult(this);

    [HttpPost("tasks/{id:long}/move")]
    public async Task<IActionResult> Move(long id, MoveBody body) =>
        (await mediator.Send(new MoveTaskRequest(id, body.GroupBy, body.ToColumn, body.DueDate, body.ExpectedVersion))).ToActionResult(this);
}
