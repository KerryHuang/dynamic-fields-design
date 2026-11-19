using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>
/// 健康檢查的規則表與結果表（維運設計 §17.2.4、§17.4）。彙總交易的第一句是 <see cref="LockAsync"/>（取得檢查列的列鎖），
/// 去重靠結果表 <c>TransitionId</c> 唯一；目前投影只往後推。
/// </summary>
public interface IHealthCheckRepository
{
    /// <summary>全部規則列，不追蹤。</summary>
    Task<List<HealthCheck>> ListAsync(CancellationToken ct);
    Task<HealthCheck?> GetTrackedAsync(string code, CancellationToken ct);
    Task<bool> ExistsAsync(string code, CancellationToken ct);
    void Add(HealthCheck check);

    /// <summary>寫入探測：對自己那一列做條件式更新（<c>LastProbeUtc</c>）。</summary>
    Task<bool> TouchProbeAsync(string code, DateTime nowUtc, CancellationToken ct);
    /// <summary>彙總交易的鎖：更新 <c>LastAggregatedUtc</c>，只當列鎖用。</summary>
    Task<bool> LockAsync(string code, DateTime nowUtc, CancellationToken ct);
    Task<bool> ResultExistsAsync(string transitionId, CancellationToken ct);
    /// <summary>這些識別碼裡已經有結果列的（直送寄前核對「其實已提交」，§17.13 審查修正）。</summary>
    Task<List<string>> ExistingResultIdsAsync(IReadOnlyCollection<string> transitionIds, CancellationToken ct);
    void AddResult(HealthCheckResult result);
    /// <summary>截至 asOf（含）、晚於 sinceUtc 的結果，每個實例取最新一筆。</summary>
    Task<List<HealthCheckResult>> LatestPerInstanceAsync(string code, DateTime asOfUtc, DateTime sinceUtc, CancellationToken ct);
    /// <summary>投影的條件式更新：<c>LastCheckedUtc IS NULL OR LastCheckedUtc &lt;= asOf</c> 才覆寫。</summary>
    Task<bool> UpdateProjectionAsync(string code, DateTime asOfUtc, HealthStatus status, long? value, string? messageCode, CancellationToken ct);
    /// <summary>下次時間只往後推。</summary>
    Task AdvanceNextDueAsync(string code, DateTime nextDueUtc, CancellationToken ct);
    /// <summary>趨勢：時間區間內依時間遞增，最多 take 筆。</summary>
    Task<List<HealthCheckResult>> ResultsAsync(string code, DateTime fromUtc, DateTime toUtc, int take, CancellationToken ct);
    Task<int> PurgeResultsBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct);
}
