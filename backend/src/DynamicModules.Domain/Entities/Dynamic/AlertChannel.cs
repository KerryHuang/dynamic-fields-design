namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>SMTP 的加密方式（維運設計 §17.2.6）。</summary>
public enum AlertSecurity { None, StartTls, SslOnConnect }

/// <summary>寄送佇列列的來源轉換；<c>Occurred</c> 是安全類每次異動，<c>Digest</c> 是摘要信。</summary>
public enum AlertTransition { Opened, Regressed, Escalated, Renotify, Occurred, Digest }

public enum AlertOutboxStatus { Pending, Sending, Sent, SentDirect, Failed, Skipped, Digested }

/// <summary>
/// 告警通道設定（維運設計 §17.4 <c>Dynamic_AlertChannel</c>）：主鍵是通道種類，O2 只有 <see cref="Smtp"/>。
/// 告警信寄到 <see cref="ToAddresses"/>（O2-U1）；信件語系 <see cref="MailLocale"/>，null＝出廠（O2-U14）。
/// </summary>
public class AlertChannel
{
    public const string Smtp = "smtp";

    public string Kind { get; set; } = Smtp;
    public bool Enabled { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public AlertSecurity Security { get; set; } = AlertSecurity.StartTls;
    public string? UserName { get; set; }
    /// <summary>Data Protection 加密後的字串（O2-T10），API 不回明文。</summary>
    public string? PasswordProtected { get; set; }
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }
    /// <summary>逗號分隔、1–20 個、應用層驗格式。</summary>
    public string? ToAddresses { get; set; }
    /// <summary><c>zh-TW</c>、<c>en-US</c>、<c>th</c>、<c>vi</c>；null＝出廠。</summary>
    public string? MailLocale { get; set; }
    /// <summary>寄送佇列領取與摘要化交易的第一句更新它，取得通道列鎖（§17.2.6）；只當鎖用。</summary>
    public DateTime? LastClaimUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastFailureUtc { get; set; }
    /// <summary>階段＋伺服器回應碼，不含例外全文。</summary>
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? ModifiedUserId { get; set; }
    public DateTime? ModifiedUtc { get; set; }
}

/// <summary>
/// 告警寄送佇列（§17.4 <c>Dynamic_AlertOutbox</c>）：短交易領取（<c>sending</c>＋領取者＋期限）→ 交易外寄 SMTP → 短交易記結果（O2-T11）。
/// 主旨與內文是寫入時以通道語系組好的快照。
/// </summary>
public class AlertOutbox
{
    public long Id { get; set; }
    public string Channel { get; set; } = AlertChannel.Smtp;
    /// <summary>摘要信為 null；不設外鍵。</summary>
    public long? IncidentId { get; set; }
    public AlertTransition Transition { get; set; }
    /// <summary>健康檢查轉換的穩定識別碼（＝<see cref="HealthCheckResult.TransitionId"/>）；其他來源為 null，不設唯一。</summary>
    public string? TransitionId { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime NextAttemptUtc { get; set; }
    /// <summary>每次領取加一。</summary>
    public int Attempts { get; set; }
    public AlertOutboxStatus Status { get; set; }
    /// <summary>領取者（實例 id＋本輪執行 id）；<c>sending</c> 時非空。</summary>
    public string? ClaimedBy { get; set; }
    public DateTime? ClaimedUntilUtc { get; set; }
    /// <summary><c>digested</c> 的列掛到哪一封摘要信。</summary>
    public long? DigestOutboxId { get; set; }
    public string? LastError { get; set; }
    public DateTime? SentUtc { get; set; }

    // 技術限制（維運設計 §17.9）
    public const int SubjectMax = 200, BodyMax = 4000;
}
