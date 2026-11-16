using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Commands;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.AppService.Queries;
using DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>應用程式套件與開關：開啟把套件展開成客戶自己的模組，關閉只停止寫入、資料與引用仍可查閱。</summary>
[ApiController]
[Route("api/v3/dynamic")]
public class AppsController(IMediator mediator) : ControllerBase
{
    public record CloseBody(bool Cascade);
    public record IntegrationEnabledBody(bool Enabled);

    [HttpGet("apps/packages")]
    public async Task<IActionResult> Packages() => (await mediator.Send(new GetAppPackagesRequest())).ToActionResult(this);

    /// <summary>1 MiB 以上在讀取本文前就回 413（RequestSizeLimit）。</summary>
    [HttpPost("apps/packages/import")]
    [RequestSizeLimit(AppJson.MaxBytes)]
    public async Task<IActionResult> Import()
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer);
        return (await mediator.Send(new ImportAppPackageRequest(buffer.ToArray()))).ToActionResult(this);
    }

    [HttpGet("apps/packages/{id}")]
    public async Task<IActionResult> Package(string id) => (await mediator.Send(new GetAppPackageRequest(id))).ToActionResult(this);

    [HttpPost("apps/packages/{id}/preview")]
    public async Task<IActionResult> Preview(string id, OpenAppBody? body) =>
        (await mediator.Send(new PreviewOpenAppRequest(id, body ?? new OpenAppBody(null, null, null, null)))).ToActionResult(this);

    [HttpPost("apps/packages/{id}/open")]
    public async Task<IActionResult> Open(string id, OpenAppBody? body) =>
        (await mediator.Send(new OpenAppRequest(id, body ?? new OpenAppBody(null, null, null, null)))).ToActionResult(this);

    [HttpGet("apps")]
    public async Task<IActionResult> List() => (await mediator.Send(new GetAppsRequest())).ToActionResult(this);

    [HttpGet("apps/{key}")]
    public async Task<IActionResult> Get(string key) => (await mediator.Send(new GetAppRequest(key))).ToActionResult(this);

    [HttpPost("apps/{key}/close")]
    public async Task<IActionResult> Close(string key, CloseBody? body) =>
        (await mediator.Send(new CloseAppRequest(key, body?.Cascade ?? false))).ToActionResult(this);

    [HttpPost("apps/{key}/reopen")]
    public async Task<IActionResult> Reopen(string key) => (await mediator.Send(new ReopenAppRequest(key))).ToActionResult(this);

    [HttpPost("apps/{key}/integrations/{code}/retry")]
    public async Task<IActionResult> RetryIntegration(string key, string code) =>
        (await mediator.Send(new RetryIntegrationRequest(key, code))).ToActionResult(this);

    [HttpPut("apps/{key}/integrations/{code}")]
    public async Task<IActionResult> SetIntegrationEnabled(string key, string code, IntegrationEnabledBody body) =>
        (await mediator.Send(new SetIntegrationEnabledRequest(key, code, body.Enabled))).ToActionResult(this);

    [HttpDelete("apps/{key}")]
    public async Task<IActionResult> Delete(string key, [FromQuery] int? purged) =>
        (await mediator.Send(new DeleteAppRequest(key, purged))).ToActionResult(this);

    /// <summary>由目前的成員模組重新產生套件檔；表達不了而略過的項目放在 X-Export-Warnings 標頭（URL 編碼、| 分隔）。</summary>
    [HttpGet("apps/{key}/export")]
    public async Task<IActionResult> Export(string key)
    {
        var result = await mediator.Send(new ExportAppRequest(key));
        if (!result.IsSuccess || result.Data is not AppExportResult export) return result.ToActionResult(this);
        if (export.Warnings.Count > 0)
            Response.Headers["X-Export-Warnings"] = string.Join('|', export.Warnings.Select(Uri.EscapeDataString));
        return File(export.File.Content, export.File.ContentType, export.File.FileName);
    }
}
