using System.Text.Json;
using DynamicModules.Application.Dynamic.ViewService.Commands.CreateView;
using DynamicModules.Application.Dynamic.ViewService.Commands.DeleteView;
using DynamicModules.Application.Dynamic.ViewService.Commands.UpdateView;
using DynamicModules.Application.Dynamic.ViewService.Queries.GetViews;
using DynamicModules.Domain.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic")]
public class ViewsController(IMediator mediator) : ControllerBase
{
    public record CreateViewBody(
        I18nText Name, bool Shared, List<long> Columns, JsonElement? Filter, List<ViewSort> Sort, bool IsDefault);
    public record UpdateViewBody(I18nText Name, List<long> Columns, JsonElement? Filter, List<ViewSort> Sort, bool IsDefault);

    [HttpGet("modules/{key}/views")]
    public async Task<IActionResult> List(string key) =>
        (await mediator.Send(new GetViewsRequest(key))).ToActionResult(this);

    [HttpPost("modules/{key}/views")]
    public async Task<IActionResult> Create(string key, CreateViewBody body) =>
        (await mediator.Send(new CreateViewRequest(
            key, body.Name, body.Shared, body.Columns, body.Filter, body.Sort, body.IsDefault))).ToActionResult(this);

    [HttpPut("views/{id:long}")]
    public async Task<IActionResult> Update(long id, UpdateViewBody body) =>
        (await mediator.Send(new UpdateViewRequest(id, body.Name, body.Columns, body.Filter, body.Sort, body.IsDefault)))
        .ToActionResult(this);

    [HttpDelete("views/{id:long}")]
    public async Task<IActionResult> Delete(long id) =>
        (await mediator.Send(new DeleteViewRequest(id))).ToActionResult(this);
}
