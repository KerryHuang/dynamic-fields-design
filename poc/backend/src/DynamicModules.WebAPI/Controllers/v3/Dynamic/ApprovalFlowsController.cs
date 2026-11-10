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

    [HttpDelete("approval-flows/{key}")]
    public async Task<IActionResult> Delete(string key) =>
        (await mediator.Send(new DeleteApprovalFlowRequest(key))).ToActionResult(this);
}
