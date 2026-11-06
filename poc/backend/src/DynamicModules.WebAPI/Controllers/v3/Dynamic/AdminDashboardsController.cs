using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DashboardService.Commands.AdminCreateDashboard;
using DynamicModules.Application.Dynamic.DashboardService.Commands.AdminDeleteDashboard;
using DynamicModules.Application.Dynamic.DashboardService.Commands.AdminReorderDashboards;
using DynamicModules.Application.Dynamic.DashboardService.Commands.AdminSetDashboardAssignments;
using DynamicModules.Application.Dynamic.DashboardService.Commands.AdminUpdateDashboard;
using DynamicModules.Application.Dynamic.DashboardService.Queries.AdminGetDashboard;
using DynamicModules.Application.Dynamic.DashboardService.Queries.AdminGetDashboards;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>系統管理 → 指派版儀表板；系統管理員檢查在 SystemAdminBehavior。</summary>
[ApiController]
[Route("api/v3/dynamic/admin/dashboards")]
public class AdminDashboardsController(IMediator mediator) : ControllerBase
{
    public record CreateBody(string? Name, JsonElement? Layout);

    public record UpdateBody(string? Name, JsonElement? Layout, int? Version);

    public record AssignmentsBody(List<long>? GroupIds);

    public record OrderBody(List<long>? Ids);

    [HttpGet]
    public async Task<IActionResult> List() => (await mediator.Send(new AdminGetDashboardsRequest())).ToActionResult(this);

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id) => (await mediator.Send(new AdminGetDashboardRequest(id))).ToActionResult(this);

    [HttpPost]
    public async Task<IActionResult> Create(CreateBody body) =>
        DashboardsController.ParseLayout(body.Layout) is not { } layout
            ? DashboardsController.Bad(this, "layout", "版面格式錯誤")
            : (await mediator.Send(new AdminCreateDashboardRequest(body.Name ?? "", layout))).ToActionResult(this);

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, UpdateBody body) =>
        DashboardsController.ParseLayout(body.Layout) is not { } layout
            ? DashboardsController.Bad(this, "layout", "版面格式錯誤")
            : body.Version is not { } version
                ? DashboardsController.Bad(this, "version", "必填")
                : (await mediator.Send(new AdminUpdateDashboardRequest(id, body.Name ?? "", layout, version))).ToActionResult(this);

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id) => (await mediator.Send(new AdminDeleteDashboardRequest(id))).ToActionResult(this);

    [HttpPut("{id:long}/assignments")]
    public async Task<IActionResult> SetAssignments(long id, AssignmentsBody body) =>
        body.GroupIds is null
            ? DashboardsController.Bad(this, "groupIds", "必填")
            : (await mediator.Send(new AdminSetDashboardAssignmentsRequest(id, body.GroupIds))).ToActionResult(this);

    [HttpPut("order")]
    public async Task<IActionResult> Reorder(OrderBody body) =>
        body.Ids is null
            ? DashboardsController.Bad(this, "ids", "必填")
            : (await mediator.Send(new AdminReorderDashboardsRequest(body.Ids))).ToActionResult(this);
}
