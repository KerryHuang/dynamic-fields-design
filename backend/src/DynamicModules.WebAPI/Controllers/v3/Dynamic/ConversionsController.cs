using DynamicModules.Application.Dynamic.ConversionService.Commands.ConvertRecord;
using DynamicModules.Application.Dynamic.ConversionService.Commands.DeleteConversion;
using DynamicModules.Application.Dynamic.ConversionService.Commands.SaveConversion;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using DynamicModules.Application.Dynamic.ConversionService.Queries.GetConversions;
using DynamicModules.Application.Dynamic.ConversionService.Queries.GetConversionInputs;
using DynamicModules.Application.Dynamic.ConversionService.Queries.GetRecordConversions;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic")]
public class ConversionsController(IMediator mediator) : ControllerBase
{
    [HttpGet("conversions")]
    public async Task<IActionResult> List() =>
        (await mediator.Send(new GetConversionsRequest())).ToActionResult(this);

    [HttpGet("conversions/{key}")]
    public async Task<IActionResult> Get(string key) =>
        (await mediator.Send(new GetConversionsRequest(key))).ToActionResult(this);

    [HttpPost("conversions")]
    public async Task<IActionResult> Create(ConversionInput body) =>
        (await mediator.Send(new CreateConversionRequest(body))).ToActionResult(this);

    [HttpPut("conversions/{key}")]
    public async Task<IActionResult> Update(string key, ConversionInput body) =>
        (await mediator.Send(new UpdateConversionRequest(key, body))).ToActionResult(this);

    [HttpDelete("conversions/{key}")]
    public async Task<IActionResult> Delete(string key) =>
        (await mediator.Send(new DeleteConversionRequest(key))).ToActionResult(this);

    [HttpGet("records/{id:long}/conversions")]
    public async Task<IActionResult> ForRecord(long id) =>
        (await mediator.Send(new GetRecordConversionsRequest(id))).ToActionResult(this);

    [HttpGet("records/{id:long}/conversions/{key}/inputs")]
    public async Task<IActionResult> Inputs(long id, string key) =>
        (await mediator.Send(new GetConversionInputsRequest(id, key))).ToActionResult(this);

    public record ConvertBody(string? RowVersion, Dictionary<string, System.Text.Json.JsonElement?>? Values, List<ConvertLineInput>? Lines);

    [HttpPost("records/{id:long}/conversions/{key}/convert")]
    public async Task<IActionResult> Convert(long id, string key, ConvertBody body) =>
        (await mediator.Send(new ConvertRecordRequest(id, key, body.RowVersion, body.Values, body.Lines))).ToActionResult(this);
}
