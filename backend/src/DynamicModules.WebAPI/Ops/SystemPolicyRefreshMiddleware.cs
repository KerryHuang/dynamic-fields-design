using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PolicyService;

namespace DynamicModules.WebAPI.Ops;

/// <summary>
/// 每個請求開始時，已安裝語系快照與全系統政策快照過期就重載（只讓一個請求跑，其他沿用舊值、不等待；讀不到也不讓請求失敗）。
/// 語系快照在前：政策快照的語系政策用已安裝語系正規化，兩者的一致性另由 generation 保證（語系包 i4a 計畫判斷 5）。
/// </summary>
public sealed class SystemPolicyRefreshMiddleware(RequestDelegate next, LocaleRegistry registry, CachedSystemPolicy cache)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        await registry.EnsureFreshAsync(ctx.RequestAborted);
        await cache.EnsureFreshAsync(ctx.RequestAborted);
        await next(ctx);
    }
}
