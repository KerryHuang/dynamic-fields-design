using DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteBlock;
using DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteField;
using DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteModule;
using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Application.Dynamic.DeleteService.Queries.CheckDelete;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>模組、欄位、區塊的安全刪除；purged 是預檢回報的軟刪除記錄筆數，刪除時必須帶上。</summary>
[ApiController]
[Route("api/v3/dynamic")]
public class DeletionController(IMediator mediator) : ControllerBase
{
    [HttpGet("modules/{key}/delete-check")]
    public async Task<IActionResult> CheckModule(string key) =>
        (await mediator.Send(new CheckDeleteRequest(DeleteTarget.Module, key, 0))).ToActionResult(this);

    [HttpDelete("modules/{key}")]
    public async Task<IActionResult> DeleteModule(string key, [FromQuery] int? purged) =>
        (await mediator.Send(new DeleteModuleRequest(key, purged))).ToActionResult(this);

    [HttpGet("fields/{id:long}/delete-check")]
    public async Task<IActionResult> CheckField(long id) =>
        (await mediator.Send(new CheckDeleteRequest(DeleteTarget.Field, null, id))).ToActionResult(this);

    [HttpDelete("fields/{id:long}")]
    public async Task<IActionResult> DeleteField(long id, [FromQuery] int? purged) =>
        (await mediator.Send(new DeleteFieldRequest(id, purged))).ToActionResult(this);

    [HttpGet("blocks/{id:long}/delete-check")]
    public async Task<IActionResult> CheckBlock(long id) =>
        (await mediator.Send(new CheckDeleteRequest(DeleteTarget.Block, null, id))).ToActionResult(this);

    [HttpDelete("blocks/{id:long}")]
    public async Task<IActionResult> DeleteBlock(long id, [FromQuery] int? purged) =>
        (await mediator.Send(new DeleteBlockRequest(id, purged))).ToActionResult(this);
}
