using DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;
using DynamicModules.Application.Dynamic.TemplateService.Commands.ImportTemplate;
using DynamicModules.Application.Dynamic.TemplateService.Commands.InstantiateTemplate;
using DynamicModules.Application.Dynamic.TemplateService.Queries.ExportTemplate;
using DynamicModules.Application.Dynamic.TemplateService.Queries.GetTemplates;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic")]
public class TemplatesController(IMediator mediator) : ControllerBase
{
    public record InstantiateBody(List<InstantiateModuleInput>? Modules, List<string>? ExcludeFields);

    [HttpGet("templates")]
    public async Task<IActionResult> List() =>
        (await mediator.Send(new GetTemplatesRequest())).ToActionResult(this);

    [HttpPost("templates/{key}/instantiate")]
    public async Task<IActionResult> Instantiate(string key, InstantiateBody body) =>
        (await mediator.Send(new InstantiateTemplateRequest(key, body.Modules ?? [], body.ExcludeFields ?? [])))
        .ToActionResult(this);

    [HttpPost("templates/import")]
    [RequestSizeLimit(1024 * 1024)]
    public async Task<IActionResult> Import()
    {
        using var reader = new StreamReader(Request.Body);
        return (await mediator.Send(new ImportTemplateRequest(await reader.ReadToEndAsync()))).ToActionResult(this);
    }

    [HttpGet("modules/{key}/export-template")]
    public async Task<IActionResult> Export(string key)
    {
        var result = await mediator.Send(new ExportTemplateRequest(key));
        return result.IsSuccess && result.Data is ExportFile file
            ? File(file.Content, file.ContentType, file.FileName)
            : result.ToActionResult(this);
    }
}
