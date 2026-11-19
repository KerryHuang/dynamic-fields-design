using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.OpsService.Alerts;

/// <summary>
/// 寄一封信要用的 SMTP 設定（密碼仍是加密字串，寄送時才解；§17.2.6「資料庫寫不進時的直送」）：
/// 連線與寄件人來自核心寄信設定（<see cref="MailSettings"/>），收件人與信件語系來自告警通道或呼叫端。
/// </summary>
public sealed record SmtpSettings(
    string Host, int Port, AlertSecurity Security, string? UserName, string? PasswordProtected, string FromAddress, string? FromName,
    IReadOnlyList<string> ToAddresses, string? MailLocale)
{
    /// <summary>
    /// 告警信可以寄：寄信設定有主機與寄件地址，通道啟用（測試信不看啟用）且至少一個收件信箱。
    /// </summary>
    public static SmtpSettings? From(MailSettings? mail, AlertChannel? channel, bool requireEnabled = true) =>
        channel is not null && (channel.Enabled || !requireEnabled) && AlertAddresses.Split(channel.ToAddresses) is { Count: > 0 } to
            ? To(mail, to, channel.MailLocale)
            : null;

    /// <summary>寄信設定（主機與寄件地址都有）寄給指定收件人；設定不完整回 null。</summary>
    public static SmtpSettings? To(MailSettings? mail, IReadOnlyList<string> to, string? mailLocale) =>
        mail is { Configured: true } m
            ? new SmtpSettings(m.Host!, m.Port, m.Security, m.UserName, m.PasswordProtected, m.FromAddress!, m.FromName, to, mailLocale)
            : null;
}

/// <summary>收件信箱（逗號分隔存在通道列上）。</summary>
public static class AlertAddresses
{
    public static IReadOnlyList<string> Split(string? joined) =>
        string.IsNullOrWhiteSpace(joined) ? [] : joined.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>一封要寄的信（純文字）。</summary>
public sealed record AlertMail(string Subject, string Body);

/// <summary>
/// SMTP 寄送的結果：失敗只回階段（<c>connect</c>、<c>tls</c>、<c>auth</c>、<c>send</c>）與 SMTP 回應碼，不回伺服器訊息或例外全文
/// （降低被當成探測工具的價值，§17.2.6）。
/// </summary>
public sealed record MailSendResult(bool Ok, string? Stage = null, int? ServerCode = null)
{
    public static readonly MailSendResult Success = new(true);

    public const string Connect = "connect", Tls = "tls", Auth = "auth", Send = "send";

    /// <summary>存進 <c>LastError</c> 的短字串：階段＋回應碼。</summary>
    public string Describe() => ServerCode is { } code ? $"{Stage}:{code}" : Stage ?? "";
}

/// <summary>
/// 寄 SMTP（WebAPI 以 MailKit 實作，O2-T9）：強制 STARTTLS 不退回明文、憑證一律驗證、單封逾時 30 秒；
/// 呼叫端保證不在任何交易裡呼叫（不准持全庫寫入鎖等網路）。密碼是明文，由呼叫端先解密。
/// </summary>
public interface IAlertMailSender
{
    Task<MailSendResult> SendAsync(SmtpSettings settings, string? password, AlertMail mail, CancellationToken ct);
}

/// <summary>
/// SMTP 密碼的加解密（WebAPI 以 Data Protection 實作，purpose <c>ops-alert-smtp</c>，O2-T10）。密碼搬到核心寄信設定後 purpose 名稱不改：
/// 既有的密文是用這個 purpose 加密、原樣搬過去的，換名字就解不開。
/// </summary>
public interface IAlertSecretProtector
{
    string Protect(string plain);

    /// <summary>解不開（金鑰不見、內容被改）回 null。</summary>
    string? TryUnprotect(string protectedText);
}

/// <summary>告警信的部署設定：<c>Ops:PublicUrl</c> 是信件內事件連結的網址前綴，沒設就不放連結（§17.2.6）。</summary>
public sealed record AlertMailOptions(string? PublicUrl)
{
    public static readonly AlertMailOptions None = new((string?)null);

    /// <summary>站內路徑轉成完整網址；沒設 PublicUrl 回 null。</summary>
    public string? Link(string path) => string.IsNullOrWhiteSpace(PublicUrl) ? null : PublicUrl.TrimEnd('/') + path;
}

/// <summary>寄送機制的技術限制（§17.9）。</summary>
public static class AlertLimits
{
    /// <summary>失敗後的重試間隔：第 n 次失敗後等 Delays[n-1]；第 5 次仍失敗標 failed。</summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];
    public const int MaxAttempts = 5;
    /// <summary>領取期限：寄送者當掉後多久可被重領。</summary>
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(2);
    /// <summary>單封與測試信的逾時。</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);
    /// <summary>測試信同一管理員多久一次。</summary>
    public static readonly TimeSpan TestGap = TimeSpan.FromSeconds(30);
    /// <summary>節流的計數窗口。</summary>
    public static readonly TimeSpan QuotaWindow = TimeSpan.FromHours(1);
    public const int MaxRecipients = 20;

    public static DateTime NextAttempt(DateTime nowUtc, int attempts) => nowUtc + RetryDelays[Math.Clamp(attempts, 1, RetryDelays.Length) - 1];
}
