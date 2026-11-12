using System.Diagnostics;
using System.Security.Claims;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;

namespace DynamicModules.WebAPI.Ops;

/// <summary>
/// 整條 pipeline 的第一個 middleware：定 request id（＝TraceIdentifier）、加 X-Request-Id 標頭、開 requestId scope，
/// 並在請求結束記一行（只用路由樣板，不記查詢字串）。放在例外處理之前，例外 log 才帶得到 requestId。
/// </summary>
public sealed class RequestContextMiddleware(
    RequestDelegate next, ILogger<RequestContextMiddleware> logger, TimeProvider time, CachedSystemPolicy policy)
{
    public const string RequestIdHeader = "X-Request-Id";
    public static readonly EventId RequestCompleted = new(5100, "RequestCompleted");

    public async Task InvokeAsync(HttpContext ctx)
    {
        var requestId = Activity.Current?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();
        ctx.TraceIdentifier = requestId;
        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers[RequestIdHeader] = requestId;
            return Task.CompletedTask;
        });
        var ops = RequestOpsFeature.From(ctx);
        ctx.Features.Set(ops);

        var start = time.GetTimestamp();
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["requestId"] = requestId });
        try
        {
            await next(ctx);
        }
        finally
        {
            await LogCompletedAsync(ctx, ops, time.GetElapsedTime(start));
        }
    }

    private async Task LogCompletedAsync(HttpContext ctx, RequestOpsFeature ops, TimeSpan elapsed)
    {
        var path = ctx.Request.Path;
        if (!path.StartsWithSegments("/api") || path.StartsWithSegments("/api/health")) return;

        var status = ctx.Response.StatusCode;
        var level = status switch
        {
            StatusCodes.Status503ServiceUnavailable => LogLevel.Warning, // 可重試的暫時狀況（資料庫忙碌），不是伺服器錯誤
            >= 500 => LogLevel.Error,
            >= 400 => LogLevel.Information,
            _ => await IsSlowAsync(elapsed) ? LogLevel.Warning : LogLevel.Debug,
        };
        // 4xx 也可能慢，但層級已是 Information；只有 2xx／3xx 才需要判斷慢
        logger.Log(level, RequestCompleted,
            "HTTP {Method} {Route} responded {StatusCode} in {ElapsedMs} ms user {UserId} module {ModuleKey}",
            ctx.Request.Method, ops.Route, status, (long)elapsed.TotalMilliseconds,
            ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), ops.ModuleKey);
    }

    private async Task<bool> IsSlowAsync(TimeSpan elapsed)
    {
        // 低於政策下限一定不慢，不必讀政策（資料庫出問題時請求結束不再多連一次）
        if (elapsed.TotalMilliseconds < PolicyCatalog.OpsSlowRequestMs.Min) return false;
        return elapsed.TotalMilliseconds >= await policy.IntAsync(PolicyCatalog.OpsSlowRequestMs, CancellationToken.None);
    }
}
