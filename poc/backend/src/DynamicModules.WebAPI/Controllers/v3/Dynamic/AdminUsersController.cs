using DynamicModules.Application.Dynamic.AccountService.Commands.CreateUser;
using DynamicModules.Application.Dynamic.AccountService.Commands.ResetPassword;
using DynamicModules.Application.Dynamic.AccountService.Commands.SetUserActive;
using DynamicModules.Application.Dynamic.AccountService.Commands.UpdateUser;
using DynamicModules.Application.Dynamic.AccountService.Queries.AdminListUsers;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>系統管理 → 使用者；系統管理員檢查在 SystemAdminBehavior。</summary>
[ApiController]
[Route("api/v3/dynamic/admin/users")]
public class AdminUsersController(IMediator mediator) : ControllerBase
{
    public record CreateBody(string? UserId, string? DisplayName, string? Password, List<long>? GroupIds, bool IsSystemAdmin);

    public record UpdateBody(string? DisplayName, List<long>? GroupIds, bool IsSystemAdmin);

    public record ResetBody(string? Password);

    [HttpGet]
    public async Task<IActionResult> List() => (await mediator.Send(new AdminListUsersRequest())).ToActionResult(this);

    [HttpPost]
    public async Task<IActionResult> Create(CreateBody body) =>
        (await mediator.Send(new CreateUserRequest(
            body.UserId ?? "", body.DisplayName ?? "", body.Password ?? "", body.GroupIds, body.IsSystemAdmin))).ToActionResult(this);

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, UpdateBody body) =>
        (await mediator.Send(new UpdateUserRequest(id, body.DisplayName ?? "", body.GroupIds, body.IsSystemAdmin))).ToActionResult(this);

    [HttpPost("{id}/reset-password")]
    public async Task<IActionResult> ResetPassword(string id, ResetBody body) =>
        (await mediator.Send(new ResetPasswordRequest(id, body.Password ?? ""))).ToActionResult(this);

    [HttpPost("{id}/deactivate")]
    public async Task<IActionResult> Deactivate(string id) => (await mediator.Send(new SetUserActiveRequest(id, false))).ToActionResult(this);

    [HttpPost("{id}/activate")]
    public async Task<IActionResult> Activate(string id) => (await mediator.Send(new SetUserActiveRequest(id, true))).ToActionResult(this);
}
