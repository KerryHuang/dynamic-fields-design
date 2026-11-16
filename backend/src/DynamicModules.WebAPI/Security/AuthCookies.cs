using System.Security.Claims;
using DynamicModules.Application.Dynamic.AccountService.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DynamicModules.WebAPI.Security;

public static class AuthCookies
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string StampClaim = "stamp";

    /// <summary>簽發 session cookie（不設 IsPersistent，關閉瀏覽器即失效）。</summary>
    public static Task SignInAsync(HttpContext ctx, AuthUserResponse user)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.UserId), new Claim(StampClaim, user.SecurityStamp)], Scheme);
        return ctx.SignInAsync(Scheme, new ClaimsPrincipal(identity));
    }

    public static Task SignOutAsync(HttpContext ctx) => ctx.SignOutAsync(Scheme);
}
