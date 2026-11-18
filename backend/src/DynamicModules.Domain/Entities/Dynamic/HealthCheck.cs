namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>健康檢查的狀態（維運設計 §17.5 <c>HealthCheckState.status</c>）。</summary>
public enum HealthStatus { Ok, Warning, Critical, Error, NotConfigured, NotApplicable, Pending }

/// <summary>
/// 健康檢查的規則與目前投影（維運設計 §17.4 <c>Dynamic_HealthCheck</c>，T12）：主鍵是檢查代碼（小寫），
/// 設定欄位 null＝出廠值；啟動時補列（插入，撞唯一鍵就略過）。
/// </summary>
public class HealthCheck
{
    public string Code { get; set; } = "";
    public bool? Enabled { get; set; }
    public int? IntervalMinutes { get; set; }
    /// <summary><c>database.writable</c> 是連續忙碌次數。</summary>
    public long? WarnThreshold { get; set; }
    public long? CriticalThreshold { get; set; }
    public string? ModifiedUserId { get; set; }
    public DateTime? ModifiedUtc { get; set; }
    /// <summary>只有 <c>database.writable</c> 用（寫入探測寫這欄）。</summary>
    public DateTime? LastProbeUtc { get; set; }
    /// <summary>彙總交易的第一句更新它，取得列鎖（§17.2.4）；只當鎖用，不顯示。</summary>
    public DateTime? LastAggregatedUtc { get; set; }
    /// <summary>目前投影的基準時間；只往後推，補寫較舊的結果不動它。</summary>
    public DateTime? LastCheckedUtc { get; set; }
    /// <summary>目前投影的整體狀態（多實例彙總後）。</summary>
    public HealthStatus? LastStatus { get; set; }
    public long? LastValue { get; set; }
    public string? LastMessageCode { get; set; }
    /// <summary>排程型檢查的下次時間（看門狗型只在記憶體排程，這欄僅供顯示）。</summary>
    public DateTime NextDueUtc { get; set; }
}

/// <summary>
/// 健康檢查的每一筆結果（§17.4 <c>Dynamic_HealthCheckResult</c>）。<see cref="TransitionId"/> 唯一：彙總交易的持久化處理標記，
/// 有這一列就代表同交易的事件轉換、通知、寄送佇列都已寫入（O2-T29）。
/// </summary>
public class HealthCheckResult
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public DateTime CheckedUtc { get; set; }
    public HealthStatus Status { get; set; }
    public long? Value { get; set; }
    public string? MessageCode { get; set; }
    public int DurationMs { get; set; }
    /// <summary>哪個實例跑的。</summary>
    public string InstanceId { get; set; } = "";
    /// <summary>檢查跑完時產生的穩定識別碼（32 字小寫十六進位）。</summary>
    public string TransitionId { get; set; } = "";
}
