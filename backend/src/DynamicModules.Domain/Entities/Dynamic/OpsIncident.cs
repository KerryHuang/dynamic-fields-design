namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>維運事件的來源（維運設計 §17.2.2）。</summary>
public enum IncidentSource { Server, Client, Config, Health, Security, Channel }

/// <summary>維運事件的類別：客戶設定、產品缺陷、基礎設施、安全。</summary>
public enum IncidentCategory { Config, Defect, Infra, Security }

public enum IncidentSeverity { Info, Warning, Critical }

public enum IncidentStatus { Open, Acknowledged, Resolved }

/// <summary>結案原因；手動結案以外都是自動結案。</summary>
public enum IncidentResolvedReason { Recovered, Expired, RuleResumed, RuleDeleted, Manual }

/// <summary>
/// 維運事件一列（維運設計 §17.4 <c>Dynamic_OpsIncident</c>）：一個指紋一列（全表唯一索引，O2-T4），結案後同一列重開。
/// 時間一律 UTC。
/// </summary>
public class OpsIncident
{
    public long Id { get; set; }
    public string Fingerprint { get; set; } = "";
    public int FingerprintVersion { get; set; }
    public IncidentSource Source { get; set; }
    public IncidentCategory Category { get; set; }
    public IncidentSeverity Severity { get; set; }
    public IncidentStatus Status { get; set; }
    /// <summary>例外型別全名、檢查代碼或錯誤碼。</summary>
    public string Code { get; set; } = "";
    /// <summary>標題代碼，畫面、鈴鐺、信件依語系組字（O2-T22）。</summary>
    public string TitleKey { get; set; } = "";
    /// <summary>組字用的白名單參數（代碼、數字、路由樣板），不含記錄值。</summary>
    public string? TitleParamsJson { get; set; }
    /// <summary>只有 server、client：正規化再遮蔽後的訊息，只在監控頁顯示、不外送（O2-T27）。</summary>
    public string? NormalizedMessage { get; set; }
    public string? ModuleKey { get; set; }
    public string? RuleKind { get; set; }
    public long? RuleId { get; set; }
    /// <summary>路由樣板。</summary>
    public string? Route { get; set; }
    public long OccurrenceCount { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public string FirstVersion { get; set; } = "";
    public string LastVersion { get; set; } = "";
    public string? LastRequestId { get; set; }
    /// <summary>是否重開過。</summary>
    public bool Regression { get; set; }
    public int ReopenCount { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTime? AcknowledgedUtc { get; set; }
    /// <summary>手動結案者；自動結案為 null。</summary>
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedUtc { get; set; }
    public IncidentResolvedReason? ResolvedReason { get; set; }
    /// <summary>重複提醒用。</summary>
    public DateTime? LastNotifiedUtc { get; set; }

    // 技術限制（維運設計 §17.9）
    public const int TitleParamsMax = 2000, NormalizedMessageMax = 200;
}

/// <summary>
/// 事件的原始樣本（§17.4 <c>Dynamic_OpsIncidentSample</c>；C2：只留在產品內）。外鍵到事件、刪除串聯；每件最多 <see cref="MaxPerIncident"/> 筆。
/// <c>security</c> 的樣本只填 OccurredUtc、UserId（行為者）、AuditLogId、RequestId。
/// </summary>
public class OpsIncidentSample
{
    public long Id { get; set; }
    public long IncidentId { get; set; }
    public DateTime OccurredUtc { get; set; }
    public string? RequestId { get; set; }
    public string? UserId { get; set; }
    public string? Version { get; set; }
    public string? Route { get; set; }
    /// <summary>只有 security：對應的稽核列；不設外鍵（稽核與事件各自依保留天數清）。</summary>
    public long? AuditLogId { get; set; }
    public string? RawMessage { get; set; }
    public string? RawStack { get; set; }
    /// <summary>前端。</summary>
    public string? Browser { get; set; }
    /// <summary>前端。</summary>
    public string? Locale { get; set; }

    // 技術限制（維運設計 §17.9）
    public const int MaxPerIncident = 20, RawMessageMax = 500, RawStackMax = 4000;
}
