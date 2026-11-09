using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.PermissionService.Queries.GetGroups;
using DynamicModules.Application.Dynamic.PermissionService.Queries.GetMe;
using DynamicModules.Application.Dynamic.PermissionService.Queries.GetModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Queries.GetUsers;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic")]
public class PermissionsController(IMediator mediator) : ControllerBase
{
    public record SavePermissionsBody(
        List<GroupPermissionDto>? Groups, List<UserPermissionDto>? Users,
        List<FieldPermissionDto>? Fields, List<OptionScopeDto>? Options, List<FieldGrantDto>? FieldGrants = null);

    [HttpGet("users")]
    public async Task<IActionResult> Users() => (await mediator.Send(new GetUsersRequest())).ToActionResult(this);

    [HttpGet("me")]
    public async Task<IActionResult> Me() => (await mediator.Send(new GetMeRequest())).ToActionResult(this);

    [HttpGet("groups")]
    public async Task<IActionResult> Groups() => (await mediator.Send(new GetGroupsRequest())).ToActionResult(this);

    [HttpGet("modules/{key}/permissions")]
    public async Task<IActionResult> Get(string key) =>
        (await mediator.Send(new GetModulePermissionsRequest(key))).ToActionResult(this);

    [HttpPut("modules/{key}/permissions")]
    public async Task<IActionResult> Save(string key, SavePermissionsBody body) =>
        (await mediator.Send(new SaveModulePermissionsRequest(key,
            new ModulePermissionsDto(body.Groups ?? [], body.Users ?? [], body.Fields ?? [], body.Options ?? [], body.FieldGrants))))
        .ToActionResult(this);
}
