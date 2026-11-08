using DynamicModules.Application.Dynamic.EventService;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>連動事件的設計器端點（設計 §11.2）。</summary>
[ApiController]
[Route("api/v3/dynamic")]
public class EventsController(IMediator mediator) : ControllerBase
{
    [HttpGet("events")]
    public async Task<IActionResult> List([FromQuery] string? sourceModuleKey) =>
        (await mediator.Send(new GetEventsRequest(null, sourceModuleKey))).ToActionResult(this);

    [HttpGet("events/{key}")]
    public async Task<IActionResult> Get(string key) =>
        (await mediator.Send(new GetEventsRequest(key))).ToActionResult(this);

    [HttpPost("events")]
    public async Task<IActionResult> Create(EventInput body) =>
        (await mediator.Send(new CreateEventRequest(body))).ToActionResult(this);

    [HttpPut("events/{key}")]
    public async Task<IActionResult> Update(string key, EventInput body) =>
        (await mediator.Send(new UpdateEventRequest(key, body))).ToActionResult(this);

    [HttpDelete("events/{key}")]
    public async Task<IActionResult> Delete(string key) =>
        (await mediator.Send(new DeleteEventRequest(key))).ToActionResult(this);
}
