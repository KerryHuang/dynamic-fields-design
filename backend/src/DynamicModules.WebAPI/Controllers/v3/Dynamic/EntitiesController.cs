using DynamicModules.Application.Dynamic.EntityService.Queries.GetEntityLight;
using DynamicModules.Application.Dynamic.EntityService.Queries.ListEntities;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic")]
public class EntitiesController(IMediator mediator) : ControllerBase
{
    [HttpGet("entities")]
    public async Task<IActionResult> List() =>
        (await mediator.Send(new ListEntitiesRequest())).ToActionResult(this);

    [HttpGet("entities/{entityKey}/light")]
    public async Task<IActionResult> Light(string entityKey, string? keyword, int page = 1, int size = 20) =>
        (await mediator.Send(new GetEntityLightRequest(entityKey, keyword, page, size))).ToActionResult(this);
}
