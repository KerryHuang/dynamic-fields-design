using DynamicModules.Application.Common.Security;

namespace DynamicModules.WebAPI.Security;

/// <summary>/api/v3/dynamic 底下除了 GET users 以外，X-User 缺少或未知一律回 401。</summary>
public class IdentityMiddleware(RequestDelegate next)
{
    private const string Prefix = "/api/v3/dynamic";
    private const string UsersPath = "/api/v3/dynamic/users";

    public async Task InvokeAsync(HttpContext ctx, IKnownUsers knownUsers)
    {
        var path = ctx.Request.Path.Value ?? "";
        var guarded = path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && !(HttpMethods.IsGet(ctx.Request.Method)
                 && path.TrimEnd('/').Equals(UsersPath, StringComparison.OrdinalIgnoreCase));
        if (guarded)
        {
            var user = ctx.Request.Headers["X-User"].FirstOrDefault();
            if (string.IsNullOrEmpty(user) || !knownUsers.Exists(user))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { message = "請先選擇使用者" });
                return;
            }
        }
        await next(ctx);
    }
}
