using DynamicModules.Application.Dynamic.RelationService.Queries.GetRelatedPaged;
using DynamicModules.Application.Dynamic.RelationService.Queries.GetRelatedTabs;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic")]
public class RelationsController(IMediator mediator) : ControllerBase
{
    [HttpGet("records/{id:long}/related")]
    public async Task<IActionResult> Tabs(long id) =>
        (await mediator.Send(new GetRelatedTabsRequest(id))).ToActionResult(this);

    [HttpGet("records/{id:long}/related/{fieldId:long}/paged")]
    public async Task<IActionResult> Paged(long id, long fieldId, [FromQuery] int page = 1, [FromQuery] int size = 20) =>
        (await mediator.Send(new GetRelatedPagedRequest(id, fieldId, page, size))).ToActionResult(this);
}
