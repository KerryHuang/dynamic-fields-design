using DynamicModules.Application.Dynamic.OpsService;
using System.Security.Claims;
using DynamicModules.WebAPI.Ops;
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

    /// <summary>前端回報程式錯誤：登入與否都收（SessionMiddleware 的可選登入分支）；只記 log，回 204。</summary>
    [HttpPost("ops/client-errors")]
    [TypeFilter(typeof(ClientErrorGuardFilter))]
    public async Task<IActionResult> ClientErrors(ClientErrorReport report)
    {
        var result = await mediator.Send(new ReportClientErrorRequest(report, User.FindFirstValue(ClaimTypes.NameIdentifier)));
        return result.IsSuccess ? NoContent() : result.ToActionResult(this);
    }
}
