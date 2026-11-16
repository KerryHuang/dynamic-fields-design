using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AccountService.Commands.CreateGroup;
using DynamicModules.Application.Dynamic.AccountService.Commands.DeleteGroup;
using DynamicModules.Application.Dynamic.AccountService.Commands.RenameGroup;
using DynamicModules.Application.Dynamic.AccountService.Commands.SetGroupMembers;
using DynamicModules.Application.Dynamic.AccountService.Queries.AdminListGroups;
using DynamicModules.Application.Dynamic.AccountService.Queries.GetGroupImpact;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>系統管理 → 群組；系統管理員檢查在 SystemAdminBehavior。</summary>
[ApiController]
[Route("api/v3/dynamic/admin/groups")]
public class AdminGroupsController(IMediator mediator) : ControllerBase
{
    public record NameBody(string? Name, string? Locale);

    public record MembersBody(List<string>? UserIds);

    [HttpGet]
    public async Task<IActionResult> List() => (await mediator.Send(new AdminListGroupsRequest())).ToActionResult(this);

    [HttpPost]
    public async Task<IActionResult> Create(NameBody body) =>
        (await mediator.Send(new CreateGroupRequest(body.Name ?? "", body.Locale ?? "zh-TW"))).ToActionResult(this);

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Rename(long id, NameBody body) =>
        (await mediator.Send(new RenameGroupRequest(id, body.Name ?? "", body.Locale ?? "zh-TW"))).ToActionResult(this);

    [HttpPut("{id:long}/members")]
    public async Task<IActionResult> SetMembers(long id, MembersBody body) =>
        body.UserIds is null
            ? ServiceResult.Invalid(new Dictionary<string, string[]> { ["userIds"] = ["必填"] }).ToActionResult(this)
            : (await mediator.Send(new SetGroupMembersRequest(id, body.UserIds))).ToActionResult(this);

    [HttpGet("{id:long}/impact")]
    public async Task<IActionResult> Impact(long id) => (await mediator.Send(new GetGroupImpactRequest(id))).ToActionResult(this);

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id) => (await mediator.Send(new DeleteGroupRequest(id))).ToActionResult(this);
}
