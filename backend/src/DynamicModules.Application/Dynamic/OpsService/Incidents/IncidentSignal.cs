using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.OpsService.Incidents;

/// <summary>樣本（C2：原文只留在產品內；security 只帶行為者與稽核列）。</summary>
public sealed record IncidentSampleData(
    DateTime OccurredUtc, string? RequestId, string? UserId, string? Version, string? Route,
    long? AuditLogId = null, string? RawMessage = null, string? RawStack = null, string? Browser = null, string? Locale = null);

/// <summary>
/// 一個事件訊號（維運設計 §17.2.2）：同指紋的訊號合併成一件事件。標題只帶代碼與白名單參數（<see cref="IncidentTitles"/>）。
/// <see cref="Recovered"/> 為真表示來源已恢復（健康檢查回到正常），未結的事件以 <c>recovered</c> 結案。
/// </summary>
public sealed record IncidentSignal
{
    public required string Fingerprint { get; init; }
    public required IncidentSource Source { get; init; }
    public required IncidentCategory Category { get; init; }
    public required IncidentSeverity Severity { get; init; }
    public required string Code { get; init; }
    public required string TitleKey { get; init; }
    public IReadOnlyDictionary<string, object>? TitleParams { get; init; }
    public string? NormalizedMessage { get; init; }
    public string? ModuleKey { get; init; }
    public string? RuleKind { get; init; }
    public long? RuleId { get; init; }
    public string? Route { get; init; }
    public long Count { get; init; } = 1;
    public required DateTime OccurredUtc { get; init; }
    public required string Version { get; init; }
    public string? RequestId { get; init; }
    public IncidentSampleData? Sample { get; init; }
    public bool Recovered { get; init; }
    /// <summary>通知連結另指（安全類指向那一筆稽核）；null 用事件詳細。</summary>
    public string? Link { get; init; }
    /// <summary>健康檢查的轉換識別碼與補寫資訊：寄送佇列列對照直送紀錄（§17.2.6）；其他來源為 null。</summary>
    public AlertBackfill? Backfill { get; init; }
}

/// <summary>一次訊號造成的轉換（決定要不要通知，§17.2.2「轉換與通知」）。</summary>
public enum IncidentTransition { None, Opened, Regressed, Escalated, Updated, Resolved, History }
