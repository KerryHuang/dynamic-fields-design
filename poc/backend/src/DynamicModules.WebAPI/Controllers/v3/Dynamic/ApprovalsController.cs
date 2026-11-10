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

    [HttpPost("records/{id:long}/approval/submit")]
    public async Task<IActionResult> Submit(long id, SubmitBody body) =>
        (await mediator.Send(new SubmitApprovalRequest(id, body.To, body.RecordRowVersion, body.Picks, body.Comment))).ToActionResult(this);
}
