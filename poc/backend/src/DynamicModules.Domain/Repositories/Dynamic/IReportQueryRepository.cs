using DynamicModules.Domain.Reports;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>報表的 SQL 聚合（設計：報表資料庫端聚合）。實作在同一個讀取快照內重驗欄位設定、探測資料狀態、再聚合。</summary>
public interface IReportQueryRepository
{
    Task<ReportSqlOutcome> AggregateAsync(ReportSqlQuery query, CancellationToken ct);
}
