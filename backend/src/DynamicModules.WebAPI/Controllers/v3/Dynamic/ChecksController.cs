using DynamicModules.Application.Dynamic.CheckService;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>跨記錄驗證的設計器端點（設計 §11.2）。</summary>
[ApiController]
[Route("api/v3/dynamic")]
public class ChecksController(IMediator mediator) : ControllerBase
{
    [HttpGet("checks")]
    public async Task<IActionResult> List([FromQuery] string? moduleKey) =>
        (await mediator.Send(new GetChecksRequest(null, moduleKey))).ToActionResult(this);

    [HttpGet("checks/{key}")]
    public async Task<IActionResult> Get(string key) =>
        (await mediator.Send(new GetChecksRequest(key))).ToActionResult(this);

    [HttpPost("checks")]
    public async Task<IActionResult> Create(CheckInput body) =>
        (await mediator.Send(new CreateCheckRequest(body))).ToActionResult(this);

    [HttpPut("checks/{key}")]
    public async Task<IActionResult> Update(string key, CheckInput body) =>
        (await mediator.Send(new UpdateCheckRequest(key, body))).ToActionResult(this);

    [HttpDelete("checks/{key}")]
    public async Task<IActionResult> Delete(string key) =>
        (await mediator.Send(new DeleteCheckRequest(key))).ToActionResult(this);

    [HttpPost("checks/{key}/scan")]
    public async Task<IActionResult> Scan(string key) =>
        (await mediator.Send(new ScanCheckRequest(key))).ToActionResult(this);
}
