using DynamicModules.Application.Dynamic.McpService.Tokens;
using DynamicModules.Application.Dynamic.McpService.Writes;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.McpService;

/// <summary>
/// 每把權杖今天已提交的工具呼叫數（mcp1a 計畫判斷 13、Q2）：「今天」從營運時區的午夜算起，以 UTC 時鐘換算（不用資料庫日期函式）。
/// 計入的動作是 12 個工具加不存在的工具（<see cref="McpTokenViews.CallActions"/>，與權杖清單的「今日呼叫」同一份）。
/// </summary>
public sealed class McpUsage(IAuditLogRepository audits, IOperationRepository operations, CachedSystemPolicy system, TimeProvider time)
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime + system.BusinessOffset);

    private DateTime StartUtc(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue) - system.BusinessOffset, DateTimeKind.Utc);

    public DateTime TodayStartUtc => StartUtc(Today);

    /// <summary>下一個營運時區午夜（UTC）：每日上限重新計算的時間。</summary>
    public DateTime NextResetUtc => StartUtc(Today.AddDays(1));

    public Task<int> CountTodayAsync(long tokenId, CancellationToken ct) =>
        audits.CountForTokenSinceAsync(tokenId, TodayStartUtc, McpTokenViews.CallActions, ct);

    /// <summary>計入寫入迴圈的動作：所有一般寫入工具（mcp2a 判斷 8；目標不是記錄的工具不會有記錄目標的列）。</summary>
    public static readonly IReadOnlyList<string> LoopActions =
        [.. McpToolCatalog.Production.Where(t => t.Risk == McpRisk.Write).Select(t => t.AuditCode)];

    /// <summary>這把權杖在最近 <paramref name="windowMinutes"/> 分鐘內成功（非回放、非預覽）寫同一筆記錄的次數（判斷 8）。</summary>
    public Task<int> CountLoopWritesAsync(long tokenId, long recordId, int windowMinutes, CancellationToken ct) =>
        audits.CountWritesForTargetSinceAsync(tokenId, McpWriteTargets.Record, recordId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            time.GetUtcNow().UtcDateTime.AddMinutes(-windowMinutes), LoopActions, ct);

    /// <summary>這位使用者的這把冪等鍵已有紀錄（重送）：迴圈檢查放行，交給冪等回放（判斷 8）。</summary>
    public async Task<bool> HasOperationAsync(string userId, string key, CancellationToken ct) =>
        await operations.GetAsync(userId, key, ct) is not null;
}
