using DynamicModules.Application.Dynamic.McpService.Tokens;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.McpService;

/// <summary>
/// 每把權杖今天已提交的工具呼叫數（mcp1a 計畫判斷 13、Q2）：「今天」從營運時區的午夜算起，以 UTC 時鐘換算（不用資料庫日期函式）。
/// 計入的動作是 12 個工具加不存在的工具（<see cref="McpTokenViews.CallActions"/>，與權杖清單的「今日呼叫」同一份）。
/// </summary>
public sealed class McpUsage(IAuditLogRepository audits, CachedSystemPolicy system, TimeProvider time)
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime + system.BusinessOffset);

    private DateTime StartUtc(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue) - system.BusinessOffset, DateTimeKind.Utc);

    public DateTime TodayStartUtc => StartUtc(Today);

    /// <summary>下一個營運時區午夜（UTC）：每日上限重新計算的時間。</summary>
    public DateTime NextResetUtc => StartUtc(Today.AddDays(1));

    public Task<int> CountTodayAsync(long tokenId, CancellationToken ct) =>
        audits.CountForTokenSinceAsync(tokenId, TodayStartUtc, McpTokenViews.CallActions, ct);
}
