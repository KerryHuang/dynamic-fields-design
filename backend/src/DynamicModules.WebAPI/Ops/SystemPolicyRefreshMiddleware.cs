using DynamicModules.Application.Dynamic.PolicyService;

namespace DynamicModules.WebAPI.Ops;

/// <summary>每個請求開始時，全系統政策快照過期就重載（只讓一個請求跑，其他沿用舊值、不等待；讀不到也不讓請求失敗）。</summary>
public sealed class SystemPolicyRefreshMiddleware(RequestDelegate next, CachedSystemPolicy cache)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        await cache.EnsureFreshAsync(ctx.RequestAborted);
        await next(ctx);
    }
}
