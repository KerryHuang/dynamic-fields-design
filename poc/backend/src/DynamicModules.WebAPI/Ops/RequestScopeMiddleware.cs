using System.Security.Claims;

namespace DynamicModules.WebAPI.Ops;

/// <summary>認證之後開 userId、route、moduleKey scope，只有 handler 內記的 log 帶得到（結束行靠自己的欄位）。</summary>
public sealed class RequestScopeMiddleware(RequestDelegate next, ILogger<RequestScopeMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var ops = ctx.Features.Get<RequestOpsFeature>();
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["userId"] = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier),
            ["route"] = ops?.Route,
            ["moduleKey"] = ops?.ModuleKey,
        });
        await next(ctx);
    }
}
