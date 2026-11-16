using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DashboardService.Commands.CreateDashboard;
using DynamicModules.Application.Dynamic.DashboardService.Commands.DeleteDashboard;
using DynamicModules.Application.Dynamic.DashboardService.Commands.RestoreDashboard;
using DynamicModules.Application.Dynamic.DashboardService.Commands.SaveDashboardLayout;
using DynamicModules.Application.Dynamic.DashboardService.Queries.DrillCard;
using DynamicModules.Application.Dynamic.DashboardService.Queries.GetDashboard;
using DynamicModules.Application.Dynamic.DashboardService.Queries.GetDashboards;
using DynamicModules.Application.Dynamic.DashboardService.Queries.RunCard;
using DynamicModules.Domain.Reports;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>儀表板：我看得到的（指派給我的、自己的）與版面存檔。版面用 JsonElement 接，格式錯誤回 400 而不是被 MVC 擋成另一種格式。</summary>
[ApiController]
[Route("api/v3/dynamic/dashboards")]
public class DashboardsController(IMediator mediator) : ControllerBase
{
    public record CreateBody(string? Name, JsonElement? Layout);

    public record SaveBody(string? Name, JsonElement? Layout, int? Version);

    public record RunBody(ReportDateRange? FilterRange);

    public record DrillBody(List<string>? GroupKeys, ReportDateRange? FilterRange);

    internal static IActionResult Bad(ControllerBase c, string key, string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { [key] = [message] }).ToActionResult(c);

    internal static DashboardLayout? ParseLayout(JsonElement? json)
    {
        if (json is not { ValueKind: JsonValueKind.Object } el) return null;
        try { return DashboardLayout.Parse(el.GetRawText()); }
        catch (JsonException) { return null; }
    }

    [HttpGet]
    public async Task<IActionResult> List() => (await mediator.Send(new GetDashboardsRequest())).ToActionResult(this);

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id) => (await mediator.Send(new GetDashboardRequest(id))).ToActionResult(this);

    [HttpPost]
    public async Task<IActionResult> Create(CreateBody body) =>
        ParseLayout(body.Layout) is not { } layout
            ? Bad(this, "layout", "版面格式錯誤")
            : (await mediator.Send(new CreateDashboardRequest(body.Name ?? "", layout))).ToActionResult(this);

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id) => (await mediator.Send(new DeleteDashboardRequest(id))).ToActionResult(this);

    [HttpPut("{id:long}/layout")]
    public async Task<IActionResult> SaveLayout(long id, SaveBody body) =>
        ParseLayout(body.Layout) is not { } layout
            ? Bad(this, "layout", "版面格式錯誤")
            : (await mediator.Send(new SaveDashboardLayoutRequest(id, body.Name, layout, body.Version))).ToActionResult(this);

    [HttpPost("{id:long}/restore")]
    public async Task<IActionResult> Restore(long id) => (await mediator.Send(new RestoreDashboardRequest(id))).ToActionResult(this);

    /// <summary>卡片查詢：先驗儀表板與卡片，再以觀看者的權限計算；不接受任意報表 id。</summary>
    [HttpPost("{id:long}/cards/{cardId}/run")]
    public async Task<IActionResult> RunCard(long id, string cardId, RunBody body, [FromQuery] string locale = "zh-TW") =>
        (await mediator.Send(new RunCardRequest(id, cardId, body.FilterRange, locale))).ToActionResult(this);

    [HttpPost("{id:long}/cards/{cardId}/drill")]
    public async Task<IActionResult> DrillCard(long id, string cardId, DrillBody body, [FromQuery] string locale = "zh-TW") =>
        body.GroupKeys is null
            ? Bad(this, "groupKeys", "必填")
            : (await mediator.Send(new DrillCardRequest(id, cardId, body.GroupKeys, body.FilterRange, locale))).ToActionResult(this);
}
