using System.Text.Json;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetLightRecords;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecordByHost;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecordHistory;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic")]
public class RecordsController(IMediator mediator) : ControllerBase
{
    public record CreateRecordBody(
        Dictionary<string, JsonElement?> Values, string? HostKey = null, Dictionary<string, List<DetailRowInput>>? Details = null);
    public record UpdateRecordBody(
        Dictionary<string, JsonElement?> Values, string RowVersion, Dictionary<string, List<DetailRowInput>>? Details = null);

    [HttpGet("modules/{key}/records/paged")]
    public async Task<IActionResult> Paged(
        string key, [FromQuery] int currentPage = 1, [FromQuery] int pagesSize = 10, [FromQuery] string? filter = null,
        [FromQuery(Name = "sortBy")] string[]? sortBy = null, [FromQuery(Name = "sortOrder")] string[]? sortOrder = null,
        [FromQuery] string? q = null) =>
        (await mediator.Send(new GetPagedRecordsRequest(
            key, currentPage, pagesSize, filter, sortBy ?? [], sortOrder ?? [], q))).ToActionResult(this);

    [HttpGet("modules/{key}/records/light")]
    public async Task<IActionResult> Light(string key, [FromQuery] string? keyword = null, [FromQuery] int page = 1, [FromQuery] int size = 20) =>
        (await mediator.Send(new GetLightRecordsRequest(key, keyword, page, size))).ToActionResult(this);

    [HttpPost("modules/{key}/records")]
    public async Task<IActionResult> Create(string key, CreateRecordBody body) =>
        (await mediator.Send(new CreateRecordRequest(key, body.Values, body.HostKey, body.Details))).ToActionResult(this);

    [HttpGet("modules/{key}/records/by-host/{hostKey}")]
    public async Task<IActionResult> GetByHost(string key, string hostKey) =>
        (await mediator.Send(new GetRecordByHostRequest(key, hostKey))).ToActionResult(this);

    [HttpGet("records/{id:long}")]
    public async Task<IActionResult> Get(long id) =>
        (await mediator.Send(new GetRecordRequest(id))).ToActionResult(this);

    [HttpGet("records/{id:long}/effects")]
    public async Task<IActionResult> Effects(long id) =>
        (await mediator.Send(new DynamicModules.Application.Dynamic.EventService.GetRecordEffectsRequest(id))).ToActionResult(this);

    [HttpGet("records/{id:long}/history")]
    public async Task<IActionResult> History(
        long id, [FromQuery] int currentPage = 1, [FromQuery] int pagesSize = 20, [FromQuery] string locale = "zh-TW") =>
        (await mediator.Send(new GetRecordHistoryRequest(id, currentPage, pagesSize, locale))).ToActionResult(this);

    [HttpPut("records/{id:long}")]
    public async Task<IActionResult> Update(long id, UpdateRecordBody body) =>
        (await mediator.Send(new UpdateRecordRequest(id, body.Values, body.RowVersion, body.Details))).ToActionResult(this);

    [HttpDelete("records/{id:long}")]
    public async Task<IActionResult> Delete(long id, [FromQuery] string rowVersion) =>
        (await mediator.Send(new DeleteRecordRequest(id, rowVersion))).ToActionResult(this);
}
