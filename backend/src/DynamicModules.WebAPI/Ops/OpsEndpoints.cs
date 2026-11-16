using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DynamicModules.WebAPI.Ops;

/// <summary>health（live／ready）與版本端點：不需登入，回應只含固定欄位，不含例外訊息或主機資訊。</summary>
public static class OpsEndpoints
{
    /// <summary>ready 檢查的逾時。技術限制（設計 §12）。</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(2);

    private static readonly object NotReady = new { status = "notReady", checks = new { database = "fail" } };

    public static void MapOpsEndpoints(this IEndpointRouteBuilder app)
    {
        var ok = new { status = "ok" };
        app.MapGet("/api/health", () => Results.Json(ok));
        app.MapGet("/api/health/live", () => Results.Json(ok));
        app.MapGet("/api/health/ready", ReadyAsync);
        app.MapGet("/api/version", () => Results.Json(new { version = BuildInfo.Version, commit = BuildInfo.Commit, builtAt = BuildInfo.BuiltAt }));
    }

    private static async Task<IResult> ReadyAsync(HttpContext http, HealthCheckService health, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger("DynamicModules.WebAPI.Ops.Health");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        cts.CancelAfter(ReadyTimeout);
        try
        {
            var report = await health.CheckHealthAsync(r => r.Tags.Contains("ready"), cts.Token);
            if (report.Status == HealthStatus.Healthy) return Results.Json(new { status = "ready" });
            logger.LogWarning("ready 檢查失敗：{Failed}", string.Join(",", report.Entries.Where(e => e.Value.Status != HealthStatus.Healthy).Select(e => e.Key)));
        }
        // 呼叫端 token 取消時 CheckHealthAsync 直接丟 OCE（不轉 Unhealthy）；逾時才算 503，使用者中止仍照框架處理
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning("ready 檢查逾時（{Seconds} 秒）", ReadyTimeout.TotalSeconds);
        }
        return Results.Json(NotReady, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
