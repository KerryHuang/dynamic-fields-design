namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>
/// 核心寄信設定（全系統一份，表 <c>Dynamic_MailSettings</c>）：SMTP 連線與寄件人。告警信（<see cref="AlertChannel"/>）與之後的帳號信共用這份連線，
/// 收件人、語系與節流各自管。單列：主鍵是字串 <see cref="Kind"/>，固定 <see cref="Default"/>（沿用告警通道以種類當主鍵的寫法；之後若要多組連線再加種類）。
/// 主機與寄件地址都有才算設定好。
/// </summary>
public class MailSettings
{
    public const string Default = "default";

    public string Kind { get; set; } = Default;
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public AlertSecurity Security { get; set; } = AlertSecurity.StartTls;
    public string? UserName { get; set; }
    /// <summary>Data Protection 加密後的字串，API 不回明文。purpose 仍是 <c>ops-alert-smtp</c>（從告警通道原樣搬過來的密文才解得開）。</summary>
    public string? PasswordProtected { get; set; }
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }
    public string? ModifiedUserId { get; set; }
    public DateTime? ModifiedUtc { get; set; }

    /// <summary>主機與寄件地址都有。</summary>
    public bool Configured => Host is { Length: > 0 } && FromAddress is { Length: > 0 };
}
