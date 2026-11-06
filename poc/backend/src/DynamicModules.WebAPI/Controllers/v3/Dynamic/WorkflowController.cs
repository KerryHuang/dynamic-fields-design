using DynamicModules.Application.Dynamic.WorkflowService.Commands.DeleteWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Application.Dynamic.WorkflowService.Queries.GetWorkflow;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic")]
public class WorkflowController(IMediator mediator) : ControllerBase
{
    [HttpGet("modules/{key}/workflow")]
    public async Task<IActionResult> Get(string key) =>
        (await mediator.Send(new GetWorkflowRequest(key))).ToActionResult(this);

    [HttpPut("modules/{key}/workflow")]
    public async Task<IActionResult> Save(string key, WorkflowInput body) =>
        (await mediator.Send(new SaveWorkflowRequest(key, body))).ToActionResult(this);

    [HttpDelete("modules/{key}/workflow")]
    public async Task<IActionResult> Delete(string key) =>
        (await mediator.Send(new DeleteWorkflowRequest(key))).ToActionResult(this);
}
