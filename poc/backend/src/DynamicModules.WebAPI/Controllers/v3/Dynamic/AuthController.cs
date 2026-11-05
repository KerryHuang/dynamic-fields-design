using DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;
using DynamicModules.Application.Dynamic.AccountService.Commands.Login;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.WebAPI.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic/auth")]
public class AuthController(IMediator mediator) : ControllerBase
{
    public record LoginBody(string? UserId, string? Password);

    public record ChangePasswordBody(string? OldPassword, string? NewPassword);

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginBody body) =>
        await SignInOnSuccess(await mediator.Send(new LoginRequest(body.UserId ?? "", body.Password ?? "")));

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await AuthCookies.SignOutAsync(HttpContext);
        return Ok();
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordBody body) =>
        await SignInOnSuccess(await mediator.Send(new ChangePasswordRequest(body.OldPassword ?? "", body.NewPassword ?? "")));

    /// <summary>成功就（重）發 cookie，回應不含 SecurityStamp。</summary>
    private async Task<IActionResult> SignInOnSuccess(Application.Common.ServiceResult result)
    {
        if (result.Data is not AuthUserResponse user) return result.ToActionResult(this);
        await AuthCookies.SignInAsync(HttpContext, user);
        return Ok(new { user.UserId, user.DisplayName, user.MustChangePassword });
    }
}
