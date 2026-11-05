using DynamicModules.Application.Dynamic.RuleService.Commands.SaveRules;
using DynamicModules.Application.Dynamic.RuleService.Data;
using DynamicModules.Application.Dynamic.RuleService.Queries.GetRules;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic")]
public class RulesController(IMediator mediator) : ControllerBase
{
    public record SaveRulesBody(List<RuleInput>? Rules);

    [HttpGet("modules/{key}/rules")]
    public async Task<IActionResult> Get(string key) =>
        (await mediator.Send(new GetRulesRequest(key))).ToActionResult(this);

    [HttpPut("modules/{key}/rules")]
    public async Task<IActionResult> Save(string key, SaveRulesBody body) =>
        (await mediator.Send(new SaveRulesRequest(key, body.Rules ?? []))).ToActionResult(this);
}
