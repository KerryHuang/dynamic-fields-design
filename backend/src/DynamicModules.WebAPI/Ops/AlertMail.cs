using System.Security.Cryptography;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Domain.Entities.Dynamic;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using MimeKit;

namespace DynamicModules.WebAPI.Ops;

/// <summary>
/// 以 MailKit 寄告警信（維運設計 §17.2.6、O2-T9）。安全邊界寫死（§17.9）：<c>startTls</c> 用 MailKit 的強制模式（伺服器不支援 STARTTLS 就失敗，
/// 不退回明文）、<c>sslOnConnect</c> 直接 TLS；憑證一律驗證（不設略過的回呼，走主機的信任區）；不限制目的位址（信任邊界是系統管理員）；
/// DNS 由 MailKit 每次連線時解析。失敗只回階段與 SMTP 回應碼，例外全文只進 log。
/// </summary>
public sealed class MailKitAlertMailSender(ILogger<MailKitAlertMailSender> logger) : IAlertMailSender
{
    public async Task<MailSendResult> SendAsync(SmtpSettings settings, string? password, AlertMail mail, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName ?? "", settings.FromAddress));
        foreach (var to in settings.ToAddresses) message.To.Add(MailboxAddress.Parse(to));
        message.Subject = mail.Subject;
        message.Body = new TextPart("plain") { Text = mail.Body };

        using var client = new SmtpClient { Timeout = (int)AlertLimits.SendTimeout.TotalMilliseconds };
        var stage = MailSendResult.Connect;
        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, settings.Security switch
            {
                AlertSecurity.None => SecureSocketOptions.None,
                AlertSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
                _ => SecureSocketOptions.StartTls,
            }, ct);
            stage = MailSendResult.Auth;
            if (settings.UserName is { Length: > 0 } user) await client.AuthenticateAsync(user, password ?? "", ct);
            stage = MailSendResult.Send;
            await client.SendAsync(message, ct);
        }
        catch (Exception e)
        {
            // 連線階段的 TLS 失敗（握手失敗、伺服器不支援 STARTTLS）歸 tls
            if (stage == MailSendResult.Connect && e is SslHandshakeException or System.Security.Authentication.AuthenticationException or NotSupportedException)
                stage = MailSendResult.Tls;
            int? code = e is SmtpCommandException command ? (int)command.StatusCode : null;
            logger.LogWarning(e, "告警信寄送失敗：階段 {Stage}、回應碼 {ServerCode}、主機 {Host}:{Port}", stage, code, settings.Host, settings.Port);
            return new MailSendResult(false, stage, code);
        }
        try
        {
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception e)
        {
            // 信已經被收下，QUIT 失敗不影響結果
            logger.LogDebug(e, "告警信寄出後中斷連線失敗");
        }
        return MailSendResult.Success;
    }
}

/// <summary>
/// SMTP 密碼以 Data Protection 加密（purpose <c>ops-alert-smtp</c>，與登入 cookie 同一套金鑰；O2-T10）。金鑰不見時解不開回 null。
/// 密碼已搬到核心寄信設定，purpose 名稱刻意不改：既有密文是用這個 purpose 加密、由 migration 原樣搬過去的，改名就解不開。
/// </summary>
public sealed class DataProtectionAlertSecretProtector(IDataProtectionProvider provider) : IAlertSecretProtector
{
    public const string Purpose = "ops-alert-smtp";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string plain) => _protector.Protect(plain);

    public string? TryUnprotect(string protectedText)
    {
        try
        {
            return _protector.Unprotect(protectedText);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
