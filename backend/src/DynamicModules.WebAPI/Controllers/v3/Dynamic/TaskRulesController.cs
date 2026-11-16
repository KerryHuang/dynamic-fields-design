using DynamicModules.Application.Dynamic.TaskRuleService;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>任務規則的設計器端點（設計 §5、§9）。</summary>
[ApiController]
[Route("api/v3/dynamic")]
public class TaskRulesController(IMediator mediator) : ControllerBase
{
    [HttpGet("modules/{key}/task-rules")]
    public async Task<IActionResult> List(string key) =>
        (await mediator.Send(new GetTaskRulesRequest(key))).ToActionResult(this);

    [HttpPost("modules/{key}/task-rules")]
    public async Task<IActionResult> Create(string key, TaskRuleInput body) =>
        (await mediator.Send(new CreateTaskRuleRequest(key, body))).ToActionResult(this);

    [HttpPut("modules/{key}/task-rules/{ruleKey}")]
    public async Task<IActionResult> Update(string key, string ruleKey, TaskRuleInput body) =>
        (await mediator.Send(new UpdateTaskRuleRequest(key, ruleKey, body))).ToActionResult(this);

    [HttpDelete("modules/{key}/task-rules/{ruleKey}")]
    public async Task<IActionResult> Delete(string key, string ruleKey) =>
        (await mediator.Send(new DeleteTaskRuleRequest(key, ruleKey))).ToActionResult(this);
}
