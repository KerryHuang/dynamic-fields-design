using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>
/// 告警通道設定（維運設計 §17.2.6、§17.4 <c>Dynamic_AlertChannel</c>）。寄送佇列的領取、額度、摘要化都在
/// <see cref="LockAsync"/> 這把通道列鎖之後（§17.7、O2-T31）。
/// </summary>
public interface IAlertChannelRepository
{
    /// <summary>不追蹤。</summary>
    Task<AlertChannel?> GetAsync(string kind, CancellationToken ct);
    Task<AlertChannel?> GetTrackedAsync(string kind, CancellationToken ct);
    void Add(AlertChannel channel);
    /// <summary>領取交易的第一句：更新 <c>LastClaimUtc</c>，只當列鎖用；沒有這一列回 false。</summary>
    Task<bool> LockAsync(string kind, DateTime nowUtc, CancellationToken ct);
    /// <summary>寄送成功：記時間、連續失敗歸零。</summary>
    Task RecordSuccessAsync(string kind, DateTime nowUtc, CancellationToken ct);
    /// <summary>寄送失敗：記時間與階段、連續失敗加一（<c>x = x + 1</c>）。</summary>
    Task RecordFailureAsync(string kind, DateTime nowUtc, string error, CancellationToken ct);
}

/// <summary>
/// 告警寄送佇列（§17.4 <c>Dynamic_AlertOutbox</c>）：短交易領取 → 交易外寄 SMTP → 短交易記結果（O2-T11）。
/// 領取與記結果都是條件式更新：領取 <c>WHERE (pending 且到期) OR (sending 且領取過期)</c>，記結果 <c>WHERE ClaimedBy = 我</c>。
/// </summary>
public interface IAlertOutboxRepository
{
    void Add(AlertOutbox row);
    /// <summary>額度：最近一小時寄出的（<c>SentUtc &gt;= since</c>）加上目前全部 <c>sending</c>；摘要信不算。</summary>
    Task<int> CountQuotaUsedAsync(string channel, DateTime sinceUtc, CancellationToken ct);
    /// <summary>下一封可領的（依建立順序）：到期的 <c>pending</c>（<paramref name="digestOnly"/> 時只看摘要信），或領取已過期的 <c>sending</c>。</summary>
    Task<AlertOutbox?> NextClaimableAsync(string channel, DateTime nowUtc, bool digestOnly, CancellationToken ct);
    /// <summary>條件式領取；影響 0 列（別人領走）回 false。</summary>
    Task<bool> TryClaimAsync(long id, string claimedBy, DateTime nowUtc, DateTime claimedUntilUtc, CancellationToken ct);
    /// <summary>
    /// 節流：未領取、到期、不是摘要信的 <c>pending</c> 改成 <c>digested</c>，並把 <c>NextAttemptUtc</c> 記成摘要化的時間（<c>digested</c> 不再領取，
    /// 這個欄位改當摘要化時間用，不加欄位）；回傳筆數。
    /// </summary>
    Task<int> DigestPendingAsync(string channel, DateTime nowUtc, CancellationToken ct);
    /// <summary>從 sinceUtc 起有沒有建過摘要信。</summary>
    Task<bool> DigestExistsSinceAsync(string channel, DateTime sinceUtc, CancellationToken ct);
    /// <summary>還沒掛到摘要信、摘要化時間（<c>NextAttemptUtc</c>）早於 beforeUtc 的 <c>digested</c> 列（依 Id）。</summary>
    Task<List<AlertOutbox>> ListUnattachedDigestedAsync(string channel, DateTime beforeUtc, CancellationToken ct);
    /// <summary>把列掛到摘要信（<c>WHERE Status = digested AND DigestOutboxId IS NULL</c>）；回傳筆數。</summary>
    Task<int> AttachToDigestAsync(IReadOnlyCollection<long> ids, long digestId, CancellationToken ct);
    /// <summary>記結果（<c>WHERE Id = ? AND ClaimedBy = 我 AND Status = sending</c>），同時清領取者與期限；影響 0 列回 false。</summary>
    Task<bool> TryCompleteAsync(long id, string claimedBy, AlertOutboxStatus status, DateTime nextAttemptUtc, string? error, DateTime? sentUtc,
        CancellationToken ct);
    /// <summary>直送已寄出（補寫去重返回時核對直送交接，§17.13 審查修正）：還在 <c>pending</c> 的那列記 <c>sentDirect</c>；影響 0 列回 false。</summary>
    Task<bool> MarkSentDirectAsync(string transitionId, DateTime sentUtc, CancellationToken ct);
    /// <summary>直送正在寄：還在 <c>pending</c> 的那列改由直送領著（<c>sending</c>），佇列不再領；影響 0 列回 false。</summary>
    Task<bool> ClaimForDirectAsync(string transitionId, string claimedBy, DateTime claimedUntilUtc, CancellationToken ct);
    /// <summary>
    /// 直送寄完交出：直送領著的那列（<c>WHERE TransitionId = ? AND Status = sending AND ClaimedBy = 直送</c>）記 <paramref name="status"/>，
    /// 清領取者；<c>pending</c> 立刻可領。影響 0 列回 false（佇列已經接手）。
    /// </summary>
    Task<bool> ReleaseDirectAsync(string transitionId, string claimedBy, AlertOutboxStatus status, DateTime? sentUtc, DateTime nowUtc,
        CancellationToken ct);
    /// <summary>通道停用或沒設定：還在 <c>pending</c> 的列改成 <c>skipped</c>。</summary>
    Task<int> SkipPendingAsync(string channel, CancellationToken ct);
    /// <summary>
    /// 一批已寄完的列（<c>sent</c>、<c>sentDirect</c>、<c>failed</c>、<c>skipped</c>，以及摘要信已寄完的 <c>digested</c>），建立時間早於 cutoff；
    /// <c>pending</c>、<c>sending</c> 不清（§17.2.8）。
    /// </summary>
    Task<int> PurgeBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct);
}
