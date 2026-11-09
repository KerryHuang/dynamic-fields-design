using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.FieldService.Commands.ReorderFields;
using DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateOption;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic")]
public class FieldsController(IMediator mediator) : ControllerBase
{
    public record UpdateFieldBody(FieldType Type, I18nText Label, FieldConfig Config, long BlockId);
    public record SetStatusBody(FieldStatus Status);
    public record ReorderBody(List<long> FieldIds);
    public record AddOptionBody(I18nText Label, string? Color);
    public record UpdateOptionBody(I18nText Label, string? Color, bool Active);

    [HttpPut("fields/{id:long}")]
    public async Task<IActionResult> Update(long id, UpdateFieldBody body) =>
        (await mediator.Send(new UpdateFieldRequest(id, body.Type, body.Label, body.Config, body.BlockId)))
        .ToActionResult(this);

    [HttpPut("fields/{id:long}/status")]
    public async Task<IActionResult> SetStatus(long id, SetStatusBody body) =>
        (await mediator.Send(new SetFieldStatusRequest(id, body.Status))).ToActionResult(this);

    [HttpPut("blocks/{id:long}/field-order")]
    public async Task<IActionResult> Reorder(long id, ReorderBody body) =>
        (await mediator.Send(new ReorderFieldsRequest(id, body.FieldIds))).ToActionResult(this);

    [HttpPost("fields/{id:long}/options")]
    public async Task<IActionResult> AddOption(long id, AddOptionBody body) =>
        (await mediator.Send(new AddOptionRequest(id, body.Label, body.Color, Designer: true))).ToActionResult(this);

    [HttpPut("options/{id:long}")]
    public async Task<IActionResult> UpdateOption(long id, UpdateOptionBody body) =>
        (await mediator.Send(new UpdateOptionRequest(id, body.Label, body.Color, body.Active))).ToActionResult(this);
}
