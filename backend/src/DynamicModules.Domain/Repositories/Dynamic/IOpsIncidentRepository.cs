using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>事件一次出現要累加的內容（維運設計 §17.2.2 轉換）。</summary>
public sealed record IncidentBump(long Count, DateTime SeenUtc, string Version, string? RequestId, IncidentSeverity Severity, string? TitleParamsJson);

/// <summary>
/// 維運事件與樣本（維運設計 §17.2.2、§17.4）。狀態轉換一律是條件式更新（<c>WHERE Status = 讀到的狀態</c>），
/// 影響 0 列由呼叫端重讀再判斷（§17.7）；計數用 <c>x = x + n</c>，不讀出來加完再寫回。
/// </summary>
public interface IOpsIncidentRepository
{
    Task<OpsIncident?> FindByFingerprintAsync(string fingerprint, CancellationToken ct);
    Task<OpsIncident?> GetAsync(long id, CancellationToken ct);
    /// <summary>加進追蹤；撞唯一鍵時呼叫端用 <see cref="Detach"/> 拿掉再重來。</summary>
    void Add(OpsIncident incident);
    void Detach(OpsIncident incident);

    /// <summary>未結的事件再出現：次數＋n、最近時間取較晚者、嚴重度換成給定值；reopen 為真時狀態退回開啟（升級）。</summary>
    Task<bool> TryBumpAsync(long id, IncidentStatus expected, IncidentBump bump, bool reopen, CancellationToken ct);
    /// <summary>已結案的事件再出現：同一列重開、標回歸、重開次數＋1（§17.2.2 狀態機）。</summary>
    Task<bool> TryRegressAsync(long id, IncidentBump bump, CancellationToken ct);
    Task<bool> TryResolveAsync(long id, IncidentStatus expected, IncidentResolvedReason reason, string? by, DateTime atUtc, CancellationToken ct);
    Task<bool> TryAcknowledgeAsync(long id, string by, DateTime atUtc, CancellationToken ct);
    Task MarkNotifiedAsync(long id, DateTime atUtc, CancellationToken ct);
    /// <summary>嚴重、開啟、上次通知早於 cutoff 才把通知時間換成 now；影響 1 列的呼叫端負責發重複提醒。</summary>
    Task<bool> TryClaimRenotifyAsync(long id, DateTime cutoffUtc, DateTime nowUtc, CancellationToken ct);
    Task<List<long>> ListRenotifyCandidatesAsync(DateTime cutoffUtc, int take, CancellationToken ct);
    /// <summary>這些來源未結、最近一次出現早於 cutoff 的事件自動結案（<c>expired</c>），排除 excludeCodes；回傳結案數。</summary>
    Task<int> ExpireBatchAsync(IReadOnlyCollection<IncidentSource> sources, IReadOnlyCollection<string> excludeCodes, DateTime cutoffUtc,
        DateTime nowUtc, int batchSize, CancellationToken ct);

    void AddSample(OpsIncidentSample sample);
    /// <summary>每件事件只留最近 keep 筆樣本（依時間再依 Id）。</summary>
    Task TrimSamplesAsync(long incidentId, int keep, CancellationToken ct);
    Task<List<OpsIncidentSample>> ListSamplesAsync(long incidentId, CancellationToken ct);

    Task<List<OpsIncident>> PageAsync(OpsIncidentQuery query, int take, CancellationToken ct);
    Task<List<OpsIncident>> ListUnresolvedAsync(CancellationToken ct);

    /// <summary>刪一批結案早於 cutoff 的事件（樣本隨外鍵串聯刪除）。</summary>
    Task<int> PurgeResolvedBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct);
    Task<int> PurgeSamplesBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct);
}

/// <summary>事件清單的篩選；分頁用 Id 遞減的 keyset（BeforeId）。</summary>
public sealed record OpsIncidentQuery(
    IReadOnlyCollection<IncidentStatus>? Statuses, IncidentCategory? Category, IncidentSeverity? Severity, long? BeforeId);
