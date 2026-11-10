using System.Text.Json;
using DynamicModules.Application.Dynamic.AttachmentService;
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

    [HttpGet("tasks/{id:long}/comments")]
    public async Task<IActionResult> Comments(long id) =>
        (await mediator.Send(new GetTaskCommentsRequest(id))).ToActionResult(this);

    /// <summary>只有文字的留言；附件走 multipart 的同一路徑。</summary>
    [HttpPost("tasks/{id:long}/comments")]
    [Consumes("application/json")]
    public async Task<IActionResult> AddComment(long id, TaskCommentInput body) =>
        (await mediator.Send(new AddTaskCommentRequest(id, body.Body ?? "", []))).ToActionResult(this);

    /// <summary>帶附件的留言：表單欄位 body 與 files（最多 5 個）。<see cref="TaskCommentUploadGuardFilter"/> 在收進主體之前先擋（關閉、過大）。</summary>
    [HttpPost("tasks/{id:long}/comments")]
    [Consumes("multipart/form-data")]
    [TypeFilter(typeof(TaskCommentUploadGuardFilter))]
    public async Task<IActionResult> AddCommentWithFiles(long id, [FromForm] string? body, [FromForm] List<IFormFile>? files, CancellationToken ct)
    {
        var streams = new List<Stream>();
        try
        {
            var uploads = new List<CommentFileUpload>();
            foreach (var f in files ?? [])
            {
                var stream = f.OpenReadStream();
                streams.Add(stream);
                uploads.Add(new CommentFileUpload(stream, f.FileName, f.ContentType));
            }
            return (await mediator.Send(new AddTaskCommentRequest(id, body ?? "", uploads), ct)).ToActionResult(this);
        }
        finally
        {
            foreach (var s in streams) await s.DisposeAsync();
        }
    }

    [HttpGet("tasks/{id:long}/comments/{cid:long}/attachments/{aid:long}")]
    public async Task<IActionResult> DownloadAttachment(long id, long cid, long aid, CancellationToken ct)
    {
        var result = await mediator.Send(new DownloadTaskAttachmentRequest(id, cid, aid), ct);
        return result.Data is AttachmentDownload file ? AttachmentFileResult.From(this, file) : result.ToActionResult(this);
    }

    [HttpPost("tasks/{id:long}/comments/{cid:long}/withdraw")]
    public async Task<IActionResult> WithdrawComment(long id, long cid) =>
        (await mediator.Send(new WithdrawTaskCommentRequest(id, cid))).ToActionResult(this);

    [HttpGet("users/mention-candidates")]
    public async Task<IActionResult> MentionCandidates([FromQuery] string? keyword) =>
        (await mediator.Send(new GetMentionCandidatesRequest(keyword))).ToActionResult(this);

    [HttpGet("records/{id:long}/tasks")]
    public async Task<IActionResult> ListByRecord(long id) =>
        (await mediator.Send(new GetRecordTasksRequest(id))).ToActionResult(this);

    [HttpPost("records/{id:long}/tasks")]
    public async Task<IActionResult> CreateOnRecord(long id, TaskInput body) =>
        (await mediator.Send(new CreateRecordTaskRequest(id, body))).ToActionResult(this);
}

public sealed record TaskCommentInput(string? Body);
