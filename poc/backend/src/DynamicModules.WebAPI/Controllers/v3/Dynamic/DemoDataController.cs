using DynamicModules.Application.Dynamic.DemoService.Commands.StartDemoData;
using DynamicModules.Application.Dynamic.DemoService.Queries.GetDemoDataStatus;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic/demo-data")]
public class DemoDataController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Status() =>
        (await mediator.Send(new GetDemoDataStatusRequest())).ToActionResult(this);

    [HttpPost("generate")]
    public async Task<IActionResult> Generate() =>
        (await mediator.Send(new StartDemoDataRequest())).ToActionResult(this);
}
