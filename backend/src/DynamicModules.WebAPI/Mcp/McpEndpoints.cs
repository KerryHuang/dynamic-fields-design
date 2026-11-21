using DynamicModules.Application.Dynamic.McpService;
using DynamicModules.Application.Dynamic.OpsService;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DynamicModules.WebAPI.Mcp;

/// <summary>
/// <c>/mcp</c> 的註冊（mcp1a 計畫判斷 1、11）：官方 SDK 2.4.0 的 stateless 模式（寫死清單：技術限制，不做 stdio），
/// 閘門中介軟體只掛在 <c>/mcp</c>，非 POST 的方法回 405（不讓 GET 掉進 SPA 的 fallback，B19）。
/// </summary>
public static class McpEndpoints
{
    public const string Path = "/mcp";

    /// <summary>stateless 的 MCP 只收 POST；其餘方法明確回 405（MCP 規範：不提供 SSE 串流的伺服器對 GET 回 405）。</summary>
    private static readonly string[] OtherMethods =
        [HttpMethods.Get, HttpMethods.Head, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete, HttpMethods.Options];

    public static IServiceCollection AddFormwrightMcp(this IServiceCollection services)
    {
        services.AddSingleton<McpAuthThrottle>();
        services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithListToolsHandler(McpSdkAdapter.ListToolsAsync)
            .WithCallToolHandler(McpSdkAdapter.CallToolAsync);
        services.AddOptions<McpServerOptions>().Configure<IOpsEnvironment>((o, env) =>
        {
            o.ServerInfo = new Implementation { Name = "formwright", Version = env.Version };
            o.ServerInstructions = McpTexts.ServerInstructions;
        });
        return services;
    }

    /// <summary>閘門：在 SessionMiddleware 之後、RequestScopeMiddleware 之前（log scope 的 userId 才是權杖擁有者）。</summary>
    public static IApplicationBuilder UseFormwrightMcpGate(this IApplicationBuilder app) =>
        app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments(Path), b => b.UseMiddleware<McpGateMiddleware>());

    /// <summary>放在 MapControllers 之後、wwwroot 區塊（MapFallbackToFile）之前。</summary>
    public static IEndpointRouteBuilder MapFormwrightMcp(this IEndpointRouteBuilder app)
    {
        app.MapMcp(Path);
        app.MapMethods(Path, OtherMethods, (HttpContext ctx) =>
        {
            ctx.Response.Headers.Allow = HttpMethods.Post;
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        });
        return app;
    }
}
