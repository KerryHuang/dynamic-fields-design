using System.Security.Claims;
using System.Text.Encodings.Web;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicModules.WebAPITests;

/// <summary>
/// 測試用 scheme：讀 X-User，從資料庫查使用者，有就發和真實 cookie 相同的 NameIdentifier、stamp claim，沒有就 NoResult。
/// SessionMiddleware 對它不做任何特例。
/// </summary>
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers["X-User"].FirstOrDefault();
        if (string.IsNullOrEmpty(userId)) return AuthenticateResult.NoResult();

        var db = Context.RequestServices.GetRequiredService<DynamicDbContext>();
        var stamp = await db.Set<AppUser>().AsNoTracking().Where(u => u.UserId == userId)
            .Select(u => u.SecurityStamp).FirstOrDefaultAsync();
        if (stamp is null) return AuthenticateResult.NoResult();

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId), new Claim("stamp", stamp)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
