using DynamicModules.Application.Dynamic.OpsService;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>維運資訊（設計 docs/superpowers/specs/2026-11-11-ops-observability-design.md §10）。</summary>
[ApiController]
[Route("api/v3/dynamic")]
public class OpsController(IMediator mediator) : ControllerBase
{
    [HttpGet("system/ops/info")]
    public async Task<IActionResult> Info() => (await mediator.Send(new GetOpsInfoRequest())).ToActionResult(this);
}
