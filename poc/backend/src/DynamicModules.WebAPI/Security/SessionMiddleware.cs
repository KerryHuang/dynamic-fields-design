using System.Security.Claims;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.WebAPI.Security;

/// <summary>
/// /api/v3/dynamic 底下除匿名白名單外都要登入。每個請求比對資料庫的 IsActive 與 SecurityStamp
/// （不快取：停用、重設密碼要在下一個請求生效）；必須改密碼時只放行 /auth/* 與 GET /me。
/// </summary>
public class SessionMiddleware(RequestDelegate next)
{
    private const string Prefix = "/api/v3/dynamic";
    private const string AuthPrefix = Prefix + "/auth/";

    public async Task InvokeAsync(HttpContext ctx, DynamicDbContext db)
    {
        var path = (ctx.Request.Path.Value ?? "").TrimEnd('/');
        if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || IsAnonymous(ctx.Request.Method, path))
        {
            await next(ctx);
            return;
        }

        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = ctx.User.FindFirstValue(AuthCookies.StampClaim);
        var row = ctx.User.Identity?.IsAuthenticated == true && userId is not null
            ? await db.Set<AppUser>().AsNoTracking().Where(u => u.UserId == userId)
                .Select(u => new { u.IsActive, u.SecurityStamp, u.MustChangePassword })
                .FirstOrDefaultAsync(ctx.RequestAborted)
            : null;
        if (row is null || !row.IsActive || row.SecurityStamp != stamp)
        {
            if (ctx.User.Identity?.IsAuthenticated == true) await AuthCookies.SignOutAsync(ctx);
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { message = "請先登入" });
            return;
        }

        if (row.MustChangePassword && !IsAllowedWhileMustChange(ctx.Request.Method, path))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { message = "請先變更密碼", code = "passwordChangeRequired" });
            return;
        }

        await next(ctx);
    }

    private static bool IsAnonymous(string method, string path) =>
        (HttpMethods.IsPost(method) && path.Equals(AuthPrefix + "login", StringComparison.OrdinalIgnoreCase))
        || (HttpMethods.IsGet(method) && path.Equals(AuthPrefix + "options", StringComparison.OrdinalIgnoreCase))
        || (HttpMethods.IsGet(method) && path.Equals(Prefix + "/theme", StringComparison.OrdinalIgnoreCase))
        || (HttpMethods.IsPost(method) && path.Equals(AuthPrefix + "demo-login", StringComparison.OrdinalIgnoreCase));

    private static bool IsAllowedWhileMustChange(string method, string path) =>
        path.StartsWith(AuthPrefix, StringComparison.OrdinalIgnoreCase)
        || (HttpMethods.IsGet(method) && path.Equals(Prefix + "/me", StringComparison.OrdinalIgnoreCase));
}
