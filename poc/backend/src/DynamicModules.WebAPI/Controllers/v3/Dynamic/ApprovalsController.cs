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

    [HttpPost("approval-tasks/{id:long}/approve")]
    public async Task<IActionResult> Approve(long id, ApproveBody body) =>
        (await mediator.Send(new ApproveTaskRequest(id, body.RowVersion, body.Comment, body.Values))).ToActionResult(this);

    [HttpPost("approval-tasks/{id:long}/reject")]
    public async Task<IActionResult> Reject(long id, RejectBody body) =>
        (await mediator.Send(new RejectTaskRequest(id, body.RowVersion, body.Comment, body.To))).ToActionResult(this);

    [HttpPost("approval-instances/{id:long}/withdraw")]
    public async Task<IActionResult> Withdraw(long id, WithdrawBody body) =>
        (await mediator.Send(new WithdrawApprovalRequest(id, body.RowVersion, body.Comment))).ToActionResult(this);

    [HttpPost("records/{id:long}/approval/submit")]
    public async Task<IActionResult> Submit(long id, SubmitBody body) =>
        (await mediator.Send(new SubmitApprovalRequest(id, body.To, body.RecordRowVersion, body.Picks, body.Comment))).ToActionResult(this);
}
