using DynamicModules.Application.Dynamic.FieldService.Commands.AddBlock;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateBlock;
using DynamicModules.Application.Dynamic.FieldService.Queries.GetRollupSources;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.DeactivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModules;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic")]
public class ModulesController(IMediator mediator) : ControllerBase
{
    public record CreateModuleBody(string Key, I18nText Name, string? Icon, I18nText? Section,
        ModuleKind Kind = ModuleKind.Standalone, string? HostEntityKey = null);
    public record UpdateModuleBody(I18nText Name, string? Icon, I18nText? Section, long? TitleFieldId, string? TitleTemplate = null,
        bool? TasksEnabled = null);
    public record AddBlockBody(I18nText Label);
    public record UpdateBlockBody(I18nText Label, bool Collapsed, int? Columns);
    public record AddFieldBody(long BlockId, FieldType Type, I18nText Label, FieldConfig Config);

    [HttpGet("modules")]
    public async Task<IActionResult> List() =>
        (await mediator.Send(new GetModulesRequest())).ToActionResult(this);

    [HttpGet("modules/{key}")]
    public async Task<IActionResult> Get(string key) =>
        (await mediator.Send(new GetModuleRequest(key))).ToActionResult(this);

    [HttpPost("modules")]
    public async Task<IActionResult> Create(CreateModuleBody body) =>
        (await mediator.Send(new CreateModuleRequest(body.Key, body.Name, body.Icon, body.Section, body.Kind, body.HostEntityKey))).ToActionResult(this);

    [HttpPut("modules/{key}")]
    public async Task<IActionResult> Update(string key, UpdateModuleBody body) =>
        (await mediator.Send(new UpdateModuleRequest(key, body.Name, body.Icon, body.Section, body.TitleFieldId, body.TitleTemplate, body.TasksEnabled)))
        .ToActionResult(this);

    [HttpGet("modules/{key}/rollup-sources")]
    public async Task<IActionResult> RollupSources(string key) =>
        (await mediator.Send(new GetRollupSourcesRequest(key))).ToActionResult(this);

    [HttpPost("modules/{key}/activate")]
    public async Task<IActionResult> Activate(string key) =>
        (await mediator.Send(new ActivateModuleRequest(key))).ToActionResult(this);

    [HttpPost("modules/{key}/deactivate")]
    public async Task<IActionResult> Deactivate(string key) =>
        (await mediator.Send(new DeactivateModuleRequest(key))).ToActionResult(this);

    [HttpPost("modules/{key}/blocks")]
    public async Task<IActionResult> AddBlock(string key, AddBlockBody body) =>
        (await mediator.Send(new AddBlockRequest(key, body.Label, Designer: true))).ToActionResult(this);

    [HttpPut("blocks/{id:long}")]
    public async Task<IActionResult> UpdateBlock(long id, UpdateBlockBody body) =>
        (await mediator.Send(new UpdateBlockRequest(id, body.Label, body.Collapsed, body.Columns))).ToActionResult(this);

    [HttpPost("modules/{key}/fields")]
    public async Task<IActionResult> AddField(string key, AddFieldBody body) =>
        (await mediator.Send(new AddFieldRequest(key, body.BlockId, body.Type, body.Label, body.Config, Designer: true)))
        .ToActionResult(this);
}
