using DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;
using DynamicModules.Application.Dynamic.AccountService.Commands.DemoLogin;
using DynamicModules.Application.Dynamic.AccountService.Commands.Login;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Application.Dynamic.AccountService.Queries.GetAuthOptions;
using DynamicModules.WebAPI.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic/auth")]
public class AuthController(IMediator mediator, IConfiguration config) : ControllerBase
{
    public record LoginBody(string? UserId, string? Password);

    public record DemoLoginBody(string? UserId);

    public record ChangePasswordBody(string? OldPassword, string? NewPassword);

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginBody body) =>
        await SignInOnSuccess(await mediator.Send(new LoginRequest(body.UserId ?? "", body.Password ?? "", ClientIp())));

    [HttpGet("options")]
    public async Task<IActionResult> Options() => (await mediator.Send(new GetAuthOptionsRequest())).ToActionResult(this);

    [HttpPost("demo-login")]
    public async Task<IActionResult> DemoLogin(DemoLoginBody body) =>
        await SignInOnSuccess(await mediator.Send(new DemoLoginRequest(body.UserId ?? "")));

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await AuthCookies.SignOutAsync(HttpContext);
        return Ok();
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordBody body) =>
        await SignInOnSuccess(await mediator.Send(new ChangePasswordRequest(body.OldPassword ?? "", body.NewPassword ?? "")));

    /// <summary>
    /// 登入限流用的來源 IP。站台在反向代理後面時，連線來源是代理，所有人會共用一個額度；
    /// 設了 Auth:ClientIpHeader（代理自己加、不接受客戶端帶入的那個標頭）才改讀它，格式不對就退回連線來源。
    /// 沒設定時一律忽略所有標頭，偽造的值換不到新額度。
    /// </summary>
    private string? ClientIp()
    {
        var header = config["Auth:ClientIpHeader"];
        if (!string.IsNullOrWhiteSpace(header) && Request.Headers.TryGetValue(header, out var values)
            && System.Net.IPAddress.TryParse(values.ToString().Trim(), out var fromHeader))
            return fromHeader.ToString();
        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    /// <summary>成功就（重）發 cookie，回應不含 SecurityStamp。</summary>
    private async Task<IActionResult> SignInOnSuccess(Application.Common.ServiceResult result)
    {
        if (result.Data is not AuthUserResponse user) return result.ToActionResult(this);
        await AuthCookies.SignInAsync(HttpContext, user);
        return Ok(new { user.UserId, user.DisplayName, user.MustChangePassword });
    }
}
