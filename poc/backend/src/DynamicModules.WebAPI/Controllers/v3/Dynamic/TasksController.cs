using System.Text.Json;
using DynamicModules.Application.Dynamic.TaskService;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>任務：我的任務、單筆任務、記錄上的任務（設計 docs/superpowers/specs/2026-11-10-task-management-design.md §9）。</summary>
[ApiController]
[Route("api/v3/dynamic")]
public class TasksController(IMediator mediator) : ControllerBase
{
    [HttpGet("tasks")]
    public async Task<IActionResult> List(
        [FromQuery] string tab = "assigned", [FromQuery] string filter = "open",
        [FromQuery] int currentPage = 1, [FromQuery] int pagesSize = 20) =>
        (await mediator.Send(new GetMyTasksRequest(tab, filter, currentPage, pagesSize))).ToActionResult(this);

    [HttpPost("tasks")]
    public async Task<IActionResult> Create(TaskInput body) =>
        (await mediator.Send(new CreateTaskRequest(body))).ToActionResult(this);

    [HttpGet("tasks/summary")]
    public async Task<IActionResult> Summary() =>
        (await mediator.Send(new GetTaskSummaryRequest())).ToActionResult(this);

    [HttpGet("tasks/{id:long}")]
    public async Task<IActionResult> Get(long id) =>
        (await mediator.Send(new GetTaskRequest(id))).ToActionResult(this);

    /// <summary>沒送的欄位不改，送 null 代表清除；所以收原始 JSON，不綁定成 DTO。</summary>
    [HttpPatch("tasks/{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] JsonElement body) =>
        (await mediator.Send(new UpdateTaskRequest(id, body))).ToActionResult(this);

    [HttpDelete("tasks/{id:long}")]
    public async Task<IActionResult> Delete(long id) =>
        (await mediator.Send(new DeleteTaskRequest(id))).ToActionResult(this);

    [HttpGet("records/{id:long}/tasks")]
    public async Task<IActionResult> ListByRecord(long id) =>
        (await mediator.Send(new GetRecordTasksRequest(id))).ToActionResult(this);

    [HttpPost("records/{id:long}/tasks")]
    public async Task<IActionResult> CreateOnRecord(long id, TaskInput body) =>
        (await mediator.Send(new CreateRecordTaskRequest(id, body))).ToActionResult(this);
}
