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

    /// <summary>乾跑驗證（J64）：跑與發布相同的檢查，不寫入。key 有值＝既有流程。</summary>
    [HttpPost("approval-flows/validate")]
    public async Task<IActionResult> Validate([FromQuery] string module, [FromQuery] string? key, ApprovalFlowDraftInput body) =>
        (await mediator.Send(new ValidateApprovalFlowRequest(module, key, body))).ToActionResult(this);

    /// <summary>建立只有草稿的新流程（沒有版本、強制停用，J64）。</summary>
    [HttpPost("approval-flows/draft")]
    public async Task<IActionResult> CreateDraft([FromQuery] string module, ApprovalFlowDraftInput body) =>
        (await mediator.Send(new CreateApprovalFlowDraftRequest(module, body))).ToActionResult(this);

    /// <summary>儲存草稿，同時套用名稱與目標狀態（U53）；樂觀鎖衝突回 409。</summary>
    [HttpPut("approval-flows/{key}/draft")]
    public async Task<IActionResult> SaveDraft(string key, ApprovalFlowDraftInput body) =>
        (await mediator.Send(new SaveApprovalFlowDraftRequest(key, body))).ToActionResult(this);

    /// <summary>捨棄草稿，回到已發布的版本。</summary>
    [HttpDelete("approval-flows/{key}/draft")]
    public async Task<IActionResult> DiscardDraft(string key, [FromQuery] string? baseRev, [FromQuery] bool force = false) =>
        (await mediator.Send(new DiscardApprovalFlowDraftRequest(key, baseRev, force))).ToActionResult(this);

    /// <summary>以請求內容發布（§9 全部規則），清掉草稿；回流程與警告 W1–W3。</summary>
    [HttpPost("approval-flows/{key}/publish")]
    public async Task<IActionResult> Publish(string key, ApprovalFlowDraftInput body) =>
        (await mediator.Send(new PublishApprovalFlowRequest(key, body))).ToActionResult(this);

    [HttpDelete("approval-flows/{key}")]
    public async Task<IActionResult> Delete(string key) =>
        (await mediator.Send(new DeleteApprovalFlowRequest(key))).ToActionResult(this);
}
