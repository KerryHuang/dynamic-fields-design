using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PrintService;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>列印範本的設計（設計器）、選單（有列印權限的使用者）與列印（HTML 文件，瀏覽器列印）。</summary>
[ApiController]
[Route("api/v3/dynamic")]
public class PrintTemplatesController(IMediator mediator) : ControllerBase
{
    [HttpGet("modules/{moduleKey}/print-templates")]
    public async Task<IActionResult> Options(string moduleKey) =>
        (await mediator.Send(new GetPrintTemplatesRequest(moduleKey))).ToActionResult(this);

    [HttpGet("modules/{moduleKey}/print-templates/definitions")]
    public async Task<IActionResult> Definitions(string moduleKey) =>
        (await mediator.Send(new GetPrintTemplateDefinitionsRequest(moduleKey))).ToActionResult(this);

    [HttpPost("modules/{moduleKey}/print-templates")]
    public async Task<IActionResult> Create(string moduleKey, PrintTemplateInput body) =>
        (await mediator.Send(new CreatePrintTemplateRequest(moduleKey, body))).ToActionResult(this);

    [HttpPut("modules/{moduleKey}/print-templates/{key}")]
    public async Task<IActionResult> Update(string moduleKey, string key, PrintTemplateInput body) =>
        (await mediator.Send(new UpdatePrintTemplateRequest(moduleKey, key, body))).ToActionResult(this);

    [HttpDelete("modules/{moduleKey}/print-templates/{key}")]
    public async Task<IActionResult> Delete(string moduleKey, string key) =>
        (await mediator.Send(new DeletePrintTemplateRequest(moduleKey, key))).ToActionResult(this);

    /// <summary>回完整 HTML 文件。標頭把文件鎖成「只能顯示」：沒有腳本、沒有外部資源、不被猜型別。</summary>
    [HttpGet("records/{id:long}/print/{templateKey}")]
    public async Task<IActionResult> Print(long id, string templateKey, [FromQuery] string? locale)
    {
        var result = await mediator.Send(new PrintRecordRequest(id, templateKey, locale));
        if (result.Data is not PrintedDocument document) return result.ToActionResult(this);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'";
        Response.Headers.CacheControl = "private, no-store";
        return Content(document.Html, "text/html; charset=utf-8");
    }
}
