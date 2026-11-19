using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>
/// 規則的健康狀態與失敗時間（維運設計 §17.2.7、§17.4 <c>Dynamic_RuleHealth</c>、<c>Dynamic_RuleFailure</c>）。
/// 計數、暫停、恢復一律是條件式更新（<c>x = x + n</c>、<c>WHERE PausedUtc IS NULL</c>），不讀出來改完再寫回（§17.7）。
/// 時間都是規則的評估時間（UTC，已截到微秒），不是補記時間。
/// </summary>
public interface IRuleHealthRepository
{
    /// <summary>這些規則的健康列（不追蹤）；沒有列的不出現。</summary>
    Task<List<RuleHealth>> ListAsync(string ruleKind, IReadOnlyCollection<long> ruleIds, CancellationToken ct);

    Task<RuleHealth?> GetAsync(string ruleKind, long ruleId, CancellationToken ct);

    /// <summary>
    /// 補記一次失敗（§17.2.7 <c>RecordRuleFailureRequest</c> 第 1 步）：失敗晚於 <c>StreakResetUtc</c> 才加計數；
    /// <c>LastFailureUtc</c> 取較晚者，錯誤碼與 request id 只在這次是最晚的失敗時才換。回傳影響列數（0＝還沒有列）。
    /// 第一句就是這個更新，PostgreSQL 上同一規則的並行失敗與 reset 會在這列的列鎖排隊。
    /// </summary>
    Task<int> BumpFailureAsync(string ruleKind, long ruleId, DateTime failedUtc, string errorCode, string? requestId, CancellationToken ct);

    /// <summary>加進追蹤（首次建列）；撞主鍵時呼叫端用 <see cref="Detach"/> 拿掉再改走更新。</summary>
    void Add(RuleHealth row);
    void Detach(RuleHealth row);

    /// <summary>
    /// 成功的 reset（§17.2.7 順序與 reset 協定）：只往後推（<c>StreakResetUtc IS NULL OR StreakResetUtc &lt; 評估時間</c>），
    /// 不晚於評估時間的失敗計數歸零。回傳影響列數。
    /// </summary>
    Task<int> ResetStreakAsync(string ruleKind, long ruleId, DateTime evaluatedUtc, CancellationToken ct);

    void AddFailure(RuleFailure failure);

    /// <summary>清掉這條規則不會再用到的失敗列：早於 <paramref name="windowStartUtc"/>，或不晚於 <paramref name="streakResetUtc"/>。</summary>
    Task PruneFailuresAsync(string ruleKind, long ruleId, DateTime windowStartUtc, DateTime? streakResetUtc, CancellationToken ct);

    /// <summary>窗口內（<c>from &lt;= FailedUtc &lt;= to</c>）且晚於 <paramref name="streakResetUtc"/> 的失敗列數。</summary>
    Task<int> CountFailuresAsync(string ruleKind, long ruleId, DateTime fromUtc, DateTime toUtc, DateTime? streakResetUtc, CancellationToken ct);

    /// <summary>暫停（<c>WHERE PausedUtc IS NULL</c>）：影響 1 列的交易就是觸發暫停的人。<c>SkippedWhilePaused</c> 歸零、<c>PauseCount</c> 加一。</summary>
    Task<bool> TryPauseAsync(string ruleKind, long ruleId, DateTime pausedUtc, CancellationToken ct);

    /// <summary>暫停期間略過一次（<c>WHERE PausedUtc IS NOT NULL</c>）。</summary>
    Task AddSkippedAsync(string ruleKind, long ruleId, int count, CancellationToken ct);

    /// <summary>
    /// 恢復（<c>WHERE PausedUtc IS NOT NULL</c>）：清 <c>PausedUtc</c>、記恢復時間與人、計數歸零、<c>StreakResetUtc</c> 推到恢復時間（只往後推）；
    /// <c>LastPausedUtc</c>、<c>SkippedWhilePaused</c> 留到下次暫停。同時刪掉這條規則的失敗列。回傳是否真的恢復了。
    /// </summary>
    Task<bool> TryResumeAsync(string ruleKind, long ruleId, DateTime resumedUtc, string resumedBy, CancellationToken ct);

    /// <summary>規則刪除時一起刪健康列與失敗列（沒有外鍵，§17.4）。</summary>
    Task DeleteAsync(string ruleKind, IReadOnlyCollection<long> ruleIds, CancellationToken ct);

    /// <summary><c>ops.upkeep</c>：刪一批早於 cutoff 的失敗列（Id 上界），回傳刪除筆數。</summary>
    Task<int> PurgeFailuresBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct);
}
