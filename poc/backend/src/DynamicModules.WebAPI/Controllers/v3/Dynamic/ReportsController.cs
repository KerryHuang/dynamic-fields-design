using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;
using DynamicModules.Application.Dynamic.ReportService.Commands.CopyReport;
using DynamicModules.Application.Dynamic.ReportService.Commands.CreateReport;
using DynamicModules.Application.Dynamic.ReportService.Commands.DeleteReport;
using DynamicModules.Application.Dynamic.ReportService.Commands.SetReportShares;
using DynamicModules.Application.Dynamic.ReportService.Commands.UpdateReport;
using DynamicModules.Application.Dynamic.ReportService.Queries.DrillReport;
using DynamicModules.Application.Dynamic.ReportService.Queries.ExportReport;
using DynamicModules.Application.Dynamic.ReportService.Queries.GetReport;
using DynamicModules.Application.Dynamic.ReportService.Queries.GetReports;
using DynamicModules.Application.Dynamic.ReportService.Queries.PreviewReport;
using DynamicModules.Application.Dynamic.ReportService.Queries.RunReport;
using DynamicModules.Domain.Reports;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>自助報表。定義用 JsonElement 接，缺清單時交給 handler 驗證回 400，不被 MVC 的非 null 檢查先擋成另一種格式。</summary>
[ApiController]
[Route("api/v3/dynamic/reports")]
public class ReportsController(IMediator mediator) : ControllerBase
{
    public record SaveBody(string? Name, JsonElement? Definition);

    public record SharesBody(List<long>? GroupIds);

    public record RunBody(ReportDateRange? Range);

    public record DrillBody(List<string>? GroupKeys, ReportDateRange? Range);

    private IActionResult Bad(string key, string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { [key] = [message] }).ToActionResult(this);

    private static ReportDefinition? ParseDefinition(JsonElement? json)
    {
        if (json is not { ValueKind: JsonValueKind.Object } el) return null;
        try { return ReportDefinition.Parse(el.GetRawText()); }
        catch (JsonException) { return null; }
    }

    [HttpGet]
    public async Task<IActionResult> List() => (await mediator.Send(new GetReportsRequest())).ToActionResult(this);

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id) => (await mediator.Send(new GetReportRequest(id))).ToActionResult(this);

    [HttpPost]
    public async Task<IActionResult> Create(SaveBody body) =>
        ParseDefinition(body.Definition) is not { } d
            ? Bad("definition", "報表定義格式錯誤")
            : (await mediator.Send(new CreateReportRequest(body.Name ?? "", d))).ToActionResult(this);

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, SaveBody body) =>
        ParseDefinition(body.Definition) is not { } d
            ? Bad("definition", "報表定義格式錯誤")
            : (await mediator.Send(new UpdateReportRequest(id, body.Name ?? "", d))).ToActionResult(this);

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id) => (await mediator.Send(new DeleteReportRequest(id))).ToActionResult(this);

    [HttpPost("{id:long}/copy")]
    public async Task<IActionResult> Copy(long id) => (await mediator.Send(new CopyReportRequest(id))).ToActionResult(this);

    [HttpPut("{id:long}/shares")]
    public async Task<IActionResult> SetShares(long id, SharesBody body) =>
        body.GroupIds is null
            ? Bad("groupIds", "必填")
            : (await mediator.Send(new SetReportSharesRequest(id, body.GroupIds))).ToActionResult(this);

    [HttpPost("{id:long}/run")]
    public async Task<IActionResult> Run(long id, RunBody body, [FromQuery] string locale = "zh-TW") =>
        (await mediator.Send(new RunReportRequest(id, body.Range, locale))).ToActionResult(this);

    [HttpPost("preview")]
    public async Task<IActionResult> Preview(JsonElement? body, [FromQuery] string locale = "zh-TW") =>
        ParseDefinition(body) is not { } d
            ? Bad("definition", "報表定義格式錯誤")
            : (await mediator.Send(new PreviewReportRequest(d, locale))).ToActionResult(this);

    [HttpPost("{id:long}/drill")]
    public async Task<IActionResult> Drill(long id, DrillBody body, [FromQuery] string locale = "zh-TW") =>
        body.GroupKeys is null
            ? Bad("groupKeys", "必填")
            : (await mediator.Send(new DrillReportRequest(id, body.GroupKeys, body.Range, locale))).ToActionResult(this);

    [HttpGet("{id:long}/export")]
    public async Task<IActionResult> Export(long id, [FromQuery] string locale = "zh-TW")
    {
        var result = await mediator.Send(new ExportReportRequest(id, locale));
        return result.IsSuccess && result.Data is ExportFile file
            ? File(file.Content, file.ContentType, file.FileName)
            : result.ToActionResult(this);
    }
}
