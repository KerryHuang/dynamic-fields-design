using DynamicModules.Application.Dynamic.ApprovalService;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>簽核流程的設計器端點（簽核設計 §15）。</summary>
[ApiController]
[Route("api/v3/dynamic")]
public class ApprovalFlowsController(IMediator mediator) : ControllerBase
{
    public sealed record ReorderBody(string Module, long ToOptionId, List<string> Keys);
    public sealed record PreviewBody(long? RecordId, Dictionary<string, System.Text.Json.JsonElement?>? Values, string InitiatorUserId);

    public sealed record DraftPreviewBody(
        ApprovalFlowInput Draft, long? RecordId, Dictionary<string, System.Text.Json.JsonElement?>? Values, string InitiatorUserId);

    [HttpGet("approval-flows")]
    public async Task<IActionResult> List([FromQuery] string module) =>
        (await mediator.Send(new GetApprovalFlowsRequest(module))).ToActionResult(this);

    [HttpPost("approval-flows")]
    public async Task<IActionResult> Create([FromQuery] string module, ApprovalFlowInput body) =>
        (await mediator.Send(new CreateApprovalFlowRequest(module, body))).ToActionResult(this);

    [HttpPut("approval-flows/order")]
    public async Task<IActionResult> Reorder(ReorderBody body) =>
        (await mediator.Send(new ReorderApprovalFlowsRequest(body.Module, body.ToOptionId, body.Keys))).ToActionResult(this);

    [HttpGet("approval-flows/{key}")]
    public async Task<IActionResult> Get(string key) =>
        (await mediator.Send(new GetApprovalFlowRequest(key))).ToActionResult(this);

    [HttpPut("approval-flows/{key}")]
    public async Task<IActionResult> Update(string key, ApprovalFlowInput body) =>
        (await mediator.Send(new UpdateApprovalFlowRequest(key, body))).ToActionResult(this);

    /// <summary>設計器試算編輯中的流程（不寫入）：流程還沒存也能試。</summary>
    [HttpPost("approval-flows/preview")]
    public async Task<IActionResult> PreviewDraft([FromQuery] string module, DraftPreviewBody body) =>
        (await mediator.Send(new PreviewApprovalDraftRequest(module, body.Draft, body.RecordId, body.Values, body.InitiatorUserId))).ToActionResult(this);

    /// <summary>設計器試算（簽核設計 §5.4）：不寫入。</summary>
    [HttpPost("approval-flows/{key}/preview")]
    public async Task<IActionResult> Preview(string key, PreviewBody body) =>
        (await mediator.Send(new PreviewApprovalFlowRequest(key, body.RecordId, body.Values, body.InitiatorUserId))).ToActionResult(this);

    [HttpDelete("approval-flows/{key}")]
    public async Task<IActionResult> Delete(string key) =>
        (await mediator.Send(new DeleteApprovalFlowRequest(key))).ToActionResult(this);
}
