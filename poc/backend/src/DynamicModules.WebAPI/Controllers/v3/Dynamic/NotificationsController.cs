using DynamicModules.Application.Dynamic.NotificationService;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>站內通知：清單（含未讀數，讀取時補提醒）、單則與全部標為已讀（設計 §6、§9）。</summary>
[ApiController]
[Route("api/v3/dynamic/notifications")]
public class NotificationsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int take = 20) =>
        (await mediator.Send(new GetNotificationsRequest(take))).ToActionResult(this);

    [HttpPost("{id:long}/read")]
    public async Task<IActionResult> Read(long id) =>
        (await mediator.Send(new MarkNotificationReadRequest(id))).ToActionResult(this);

    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll() =>
        (await mediator.Send(new MarkAllNotificationsReadRequest())).ToActionResult(this);
}
