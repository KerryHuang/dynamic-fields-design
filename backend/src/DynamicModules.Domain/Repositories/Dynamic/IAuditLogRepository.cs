using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>
/// 稽核表（維運設計 §17.2.1）：只有新增、查詢、依保留天數批次刪除；沒有修改或單筆刪除的路徑（§12、§17.9）。
/// </summary>
public interface IAuditLogRepository
{
    /// <summary>加進追蹤，跟著呼叫端下一次存檔寫入。</summary>
    void Add(AuditLogEntry entry);

    /// <summary>依 Id 遞減的 keyset 分頁；回傳最多 <paramref name="take"/> 列，不追蹤。</summary>
    Task<List<AuditLogEntry>> PageAsync(AuditLogQuery query, int take, CancellationToken ct);

    /// <summary>稽核詳細；不追蹤。</summary>
    Task<AuditLogEntry?> GetAsync(long id, CancellationToken ct);

    /// <summary>刪掉最舊的一批（最多 <paramref name="batchSize"/> 列）早於 <paramref name="cutoffUtc"/> 的列，回傳刪除筆數；要在交易型請求內呼叫。</summary>
    Task<int> PurgeBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct);
    /// <summary>某個動作在 sinceUtc（含）之後的列數（<c>security.loginFailures</c>，維運設計 §17.2.4）。</summary>
    Task<int> CountSinceAsync(string action, DateTime sinceUtc, CancellationToken ct);
    /// <summary>某把 MCP 權杖在 sinceUtc（含）之後、動作在 <paramref name="actions"/> 裡的列數（每日呼叫上限，MCP 設計 §8.2）；清單是空的回 0。</summary>
    Task<int> CountForTokenSinceAsync(long tokenId, DateTime sinceUtc, IReadOnlyCollection<string> actions, CancellationToken ct);
    /// <summary>
    /// 寫入迴圈的計數（MCP 設計 §20 C3、mcp2a 計畫判斷 8）：某把權杖在 sinceUtc（含）之後、對同一個目標、動作在 <paramref name="actions"/> 裡、
    /// 結果成功且沒有原因碼（排除回放、預覽）的列數；清單是空的回 0。走 (TargetKind, TargetId, OccurredUtc) 索引。
    /// </summary>
    Task<int> CountWritesForTargetSinceAsync(long tokenId, string targetKind, string targetId, DateTime sinceUtc,
        IReadOnlyCollection<string> actions, CancellationToken ct);
    /// <summary>同 <see cref="CountForTokenSinceAsync"/>，一次分組統計多把權杖（權杖清單的「今日呼叫」）；沒有列的權杖不在結果裡，兩個清單任一是空的回空字典。</summary>
    Task<Dictionary<long, int>> CountForTokensSinceAsync(
        IReadOnlyCollection<long> tokenIds, DateTime sinceUtc, IReadOnlyCollection<string> actions, CancellationToken ct);
}

/// <summary>稽核查詢條件；時間範圍兩端都含（預設迄時是「現在」，剛寫的列要查得到）。動作分組由呼叫端展開成 <see cref="Actions"/>（不用 LIKE 前綴，O2-T18）。</summary>
public sealed record AuditLogQuery(
    DateTime FromUtc, DateTime ToUtc, long? BeforeId = null, string? ActorUserId = null, IReadOnlyCollection<string>? Actions = null,
    AuditChannel? Channel = null, AuditResult? Result = null, string? TargetKind = null, string? TargetId = null, long? TokenId = null);
