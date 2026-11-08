using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>縮小後備載入的結果：FallbackReason 不為 null 時代表不縮小（呼叫端改載入全部），Main、Child 無意義。</summary>
public sealed record ReportNarrowedRecords(string? FallbackReason, IReadOnlyList<Record> Main, IReadOnlyList<Record> Child)
{
    public static ReportNarrowedRecords Fallback(string reason) => new(reason, [], []);
}

/// <summary>報表的 SQL 聚合（設計：報表資料庫端聚合）。實作在同一個讀取快照內重驗欄位設定、探測資料狀態、再聚合。</summary>
public interface IReportQueryRepository
{
    Task<ReportSqlOutcome> AggregateAsync(ReportSqlQuery query, CancellationToken ct);

    /// <summary>
    /// 縮小後備載入（設計 §8.1）：主模組只載入符合精確條件的記錄；有子模組時，只載入「連結指向已載入主記錄」的子記錄，
    /// 不依子模組自己的條件縮小（左合併語意）。最多回 limit 筆主記錄，超過 limit 時 Main.Count == limit，呼叫端以此判斷過大。
    /// </summary>
    Task<ReportNarrowedRecords> ListNarrowedAsync(
        ReportSqlScope main, ReportSqlNarrowing narrowing, ReportSqlScope? child, long linkFieldId, int limit, CancellationToken ct);
}
