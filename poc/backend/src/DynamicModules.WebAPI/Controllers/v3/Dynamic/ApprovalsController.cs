using DynamicModules.Application.Dynamic.ApprovalService;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>簽核的執行期端點（簽核設計 §15）；所有動作支援 Idempotency-Key。</summary>
[ApiController]
[Route("api/v3/dynamic")]
public class ApprovalsController(IMediator mediator) : ControllerBase
{
    public sealed record SubmitBody(long To, string RecordRowVersion, Dictionary<string, List<string>>? Picks, string? Comment);

    public sealed record ApproveBody(string RowVersion, string? Comment, Dictionary<string, System.Text.Json.JsonElement?>? Values);
    public sealed record RejectBody(string RowVersion, string Comment, string To);
    public sealed record WithdrawBody(string RowVersion, string? Comment);
    public sealed record AddSignBody(string RowVersion, string? Comment, Dictionary<string, System.Text.Json.JsonElement?>? Values, List<string>? UserIds, string? Mode);
    public sealed record TransferBody(string RowVersion, string UserId, string? Comment);
    public sealed record CancelBody(string RowVersion, string Comment);
    public sealed record ReassignBody(string RowVersion, string UserId, string? Comment);
    public sealed record PreviewBody(long To);

    [HttpPost("approval-tasks/{id:long}/approve")]
    public async Task<IActionResult> Approve(long id, ApproveBody body) =>
        (await mediator.Send(new ApproveTaskRequest(id, body.RowVersion, body.Comment, body.Values))).ToActionResult(this);

    [HttpPost("approval-tasks/{id:long}/reject")]
    public async Task<IActionResult> Reject(long id, RejectBody body) =>
        (await mediator.Send(new RejectTaskRequest(id, body.RowVersion, body.Comment, body.To))).ToActionResult(this);

    [HttpPost("approval-tasks/{id:long}/add-sign")]
    public async Task<IActionResult> AddSign(long id, AddSignBody body) =>
        (await mediator.Send(new AddSignTaskRequest(id, body.RowVersion, body.Comment, body.Values, body.UserIds ?? [], body.Mode ?? ""))).ToActionResult(this);

    [HttpPost("approval-tasks/{id:long}/transfer")]
    public async Task<IActionResult> Transfer(long id, TransferBody body) =>
        (await mediator.Send(new TransferTaskRequest(id, body.RowVersion, body.UserId, body.Comment))).ToActionResult(this);

    [HttpPost("approval-instances/{id:long}/withdraw")]
    public async Task<IActionResult> Withdraw(long id, WithdrawBody body) =>
        (await mediator.Send(new WithdrawApprovalRequest(id, body.RowVersion, body.Comment))).ToActionResult(this);

    [HttpPost("approval-tasks/{id:long}/reassign")]
    public async Task<IActionResult> Reassign(long id, ReassignBody body) =>
        (await mediator.Send(new ReassignTaskRequest(id, body.RowVersion, body.UserId, body.Comment))).ToActionResult(this);

    [HttpPost("approval-instances/{id:long}/cancel")]
    public async Task<IActionResult> Cancel(long id, CancelBody body) =>
        (await mediator.Send(new CancelApprovalRequest(id, body.RowVersion, body.Comment))).ToActionResult(this);

    [HttpGet("records/{id:long}/approval")]
    public async Task<IActionResult> GetRecordApproval(long id) =>
        (await mediator.Send(new GetRecordApprovalRequest(id))).ToActionResult(this);

    /// <summary>簽核中心：box＝todo、done、submitted、all；from／to 篩送出日（yyyy-MM-dd，含頭尾）；status＝實例狀態。</summary>
    [HttpGet("approval-tasks")]
    public async Task<IActionResult> Inbox([FromQuery] string box = ApprovalInboxBoxes.Todo, [FromQuery] string? module = null,
        [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null, [FromQuery] int currentPage = 1, [FromQuery] int pagesSize = 20,
        [FromQuery] string? status = null) =>
        (await mediator.Send(new GetApprovalTasksRequest(box, module, from, to, currentPage, pagesSize, status))).ToActionResult(this);

    /// <summary>送出前預覽（簽核設計 §5.4）：不寫入。</summary>
    [HttpPost("records/{id:long}/approval/preview")]
    public async Task<IActionResult> Preview(long id, PreviewBody body) =>
        (await mediator.Send(new PreviewRecordApprovalRequest(id, body.To))).ToActionResult(this);

    [HttpPost("records/{id:long}/approval/submit")]
    public async Task<IActionResult> Submit(long id, SubmitBody body) =>
        (await mediator.Send(new SubmitApprovalRequest(id, body.To, body.RecordRowVersion, body.Picks, body.Comment))).ToActionResult(this);
}
