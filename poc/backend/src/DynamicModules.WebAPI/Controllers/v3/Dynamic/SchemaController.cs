using DynamicModules.Application.Dynamic.SchemaService.Data;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetMenu;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetPreviewSchema;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic")]
public class SchemaController(IMediator mediator) : ControllerBase
{
    [HttpGet("modules/{key}/schema")]
    public async Task<IActionResult> GetSchema(string key)
    {
        var result = await mediator.Send(new GetModuleSchemaRequest(key));
        if (result.Data is not ModuleSchemaResponse schema) return result.ToActionResult(this);

        var etag = SchemaETag.For(schema.Key, schema.ETagVersion, schema.PermHash);
        Response.Headers[HeaderNames.ETag] = etag;
        var current = new EntityTagHeaderValue(etag);
        if (Request.GetTypedHeaders().IfNoneMatch.Any(t => t.Equals(EntityTagHeaderValue.Any) || t.Compare(current, useStrongComparison: false)))
            return StatusCode(StatusCodes.Status304NotModified);
        return result.ToActionResult(this);
    }

    /// <summary>設計器預覽：目前草稿以管理員權限組出的 schema，不帶 ETag。</summary>
    [HttpGet("modules/{key}/schema/preview")]
    public async Task<IActionResult> GetPreviewSchema(string key) =>
        (await mediator.Send(new GetPreviewSchemaRequest(key))).ToActionResult(this);

    [HttpGet("menu")]
    public async Task<IActionResult> GetMenu() => Ok(await mediator.Send(new GetMenuRequest()));
}
