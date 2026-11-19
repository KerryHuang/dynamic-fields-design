using System.Net.Mail;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.OpsService.Monitor;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.MailService;

// 核心寄信設定 API（帳號信與告警信共用的 SMTP 連線，使用者 2026-11-19 裁決）：讀設定、存設定、寄測試信。
// 全部只給系統管理員；開 Demo 登入時存設定與測試信回 403（同 o2c 告警通道 O2-U10）。

public sealed record MailSettingsResponse(
    string? Host, int Port, string Security, string? UserName, bool HasPassword, bool PasswordUnreadable, string? FromAddress, string? FromName,
    bool Configured, bool Editable);

public sealed record GetMailSettingsRequest : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed class GetMailSettingsHandler(IMailSettingsRepository settings, IAlertSecretProtector protector, AuthSettings auth)
    : IRequestHandler<GetMailSettingsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetMailSettingsRequest request, CancellationToken ct)
    {
        var m = await settings.GetAsync(ct) ?? new MailSettings();
        var hasPassword = m.PasswordProtected is not null;
        return ServiceResult.Success(new MailSettingsResponse(
            m.Host, m.Port, OpsMonitorReader.Camel(m.Security), m.UserName, hasPassword,
            // 金鑰不見（PostgreSQL 沒設 KeysDirectory 而重啟）時解不開：畫面要求重填（§17.2.6）
            hasPassword && protector.TryUnprotect(m.PasswordProtected!) is null,
            m.FromAddress, m.FromName, m.Configured, !auth.DemoLoginEnabled));
    }
}

/// <summary>
/// 存寄信設定。<paramref name="Password"/>：null＝不變、空字串＝清除、其他＝新密碼（Data Protection 加密後存，不存明文）。
/// 主機與寄件地址可以都空（＝未設定），只填一個時另一個必填。
/// </summary>
public sealed record UpdateMailSettingsRequest(
    string? Host, int Port, string? Security, string? UserName, string? Password, string? FromAddress, string? FromName)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => new("mailSettings.update", AuditTargets.MailSettings, MailSettings.Default);
}

public sealed class UpdateMailSettingsHandler(
    IMailSettingsRepository settings, IAlertSecretProtector protector, AuthSettings auth, ICurrentUser user, AuditContext audit, IUnitOfWork uow,
    TimeProvider time) : IRequestHandler<UpdateMailSettingsRequest, ServiceResult>
{
    // 欄位長度＝表的欄位長度（沿用 o2c 告警通道的連線欄位）
    private const int HostMax = 255, AddressMax = 255, NameMax = 100;

    public async Task<ServiceResult> Handle(UpdateMailSettingsRequest r, CancellationToken ct)
    {
        // 公開 Demo 人人是管理員，可以把主機設成任意位址、讓伺服器替他連內網或寄信（§17.2.6、O2-U10）
        if (auth.DemoLoginEnabled) return ServiceResult.Forbidden();

        var errors = new Dictionary<string, MessageRef[]>();
        void Error(string key, MessageRef m) => errors.TryAdd(key, [m]);
        var host = MailAddressRules.Blank(r.Host);
        var from = MailAddressRules.Blank(r.FromAddress);
        var security = r.Security switch
        {
            "none" => AlertSecurity.None, "startTls" => AlertSecurity.StartTls, "sslOnConnect" => AlertSecurity.SslOnConnect, _ => (AlertSecurity?)null,
        };
        if (security is null) Error("security", MessageRef.Of(MessageText.OptionInvalid));
        if (host is null && from is not null) Error("host", MessageRef.Of(MessageText.Required));
        if (host is { Length: > HostMax }) Error("host", MessageRef.Of(MessageText.TooLong, HostMax));
        if (r.Port is < 1 or > 65535) Error("port", MessageRef.Of(MessageText.HealthCheckOutOfRange, 1, 65535));
        if (from is null && host is not null) Error("fromAddress", MessageRef.Of(MessageText.Required));
        if (from is not null && (from.Length > AddressMax || !MailAddressRules.IsEmail(from))) Error("fromAddress", MessageRef.Of(MessageText.EmailFormat));
        if (MailAddressRules.Blank(r.FromName) is { Length: > NameMax }) Error("fromName", MessageRef.Of(MessageText.TooLong, NameMax));
        if (MailAddressRules.Blank(r.UserName) is { Length: > AddressMax }) Error("userName", MessageRef.Of(MessageText.TooLong, AddressMax));
        if (errors.Count > 0) return ServiceResult.Invalid(errors);

        var row = await settings.GetTrackedAsync(ct);
        if (row is null)
        {
            row = new MailSettings();
            settings.Add(row);
        }
        // 不准明文傳密碼：加密方式 none 只給以來源位址授權、不需登入的內網 relay（§17.2.6）
        var keepsPassword = r.Password is null ? row.PasswordProtected is not null : r.Password.Length > 0;
        if (security == AlertSecurity.None && (MailAddressRules.Blank(r.UserName) is not null || keepsPassword))
            return ServiceResult.Invalid("security", MessageRef.Of(MessageText.SmtpPlaintextAuth), "smtpPlaintextAuth");

        var before = Snapshot(row);
        row.Host = host;
        row.Port = r.Port;
        row.Security = security!.Value;
        row.UserName = MailAddressRules.Blank(r.UserName);
        if (r.Password is not null) row.PasswordProtected = r.Password.Length == 0 ? null : protector.Protect(r.Password);
        row.FromAddress = from;
        row.FromName = MailAddressRules.Blank(r.FromName);
        row.ModifiedUserId = user.UserId;
        row.ModifiedUtc = time.GetUtcNow().UtcDateTime;
        await uow.CommitAsync(ct);
        audit.Describe(before, Snapshot(row));
        if (r.Password is { Length: > 0 }) audit.Detail("passwordChanged", true);
        return ServiceResult.Success();
    }

    /// <summary>稽核差異：密碼只記有沒有，不記內容。</summary>
    private static object Snapshot(MailSettings m) => new
    {
        m.Host, m.Port, Security = OpsMonitorReader.Camel(m.Security), m.UserName, HasPassword = m.PasswordProtected is not null,
        m.FromAddress, m.FromName,
    };
}

/// <summary>
/// 寄信設定的測試信：不經佇列、立刻用<b>已儲存</b>的設定寄一封到寄件地址本身（不能臨時填地址，避免被當寄信工具），30 秒逾時、
/// 信件語系是這個請求的介面語系（X-Locale）。節流與告警通道測試信共用同一個 <see cref="SmtpTestGate"/>（同一管理員兩種測試信合計 30 秒一次）。
/// 不是交易型請求；稽核 <c>mailSettings.test</c> 在交易外寫（寫不進只記 log）。失敗回 422 與階段、回應碼，不回例外全文。
/// </summary>
public sealed record SendMailSettingsTestRequest : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed class SendMailSettingsTestHandler(
    IMailSettingsRepository settings, IAlertSecretProtector protector, IAlertMailSender mailer, SmtpTestGate gate, AuthSettings auth,
    ICurrentUser user, ICurrentLocale locale, IAuditLog audit, TimeProvider time) : IRequestHandler<SendMailSettingsTestRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SendMailSettingsTestRequest request, CancellationToken ct)
    {
        if (auth.DemoLoginEnabled) return ServiceResult.Forbidden();
        var row = await settings.GetAsync(ct);
        var smtp = row is { FromAddress: { } from } ? SmtpSettings.To(row, [from], locale.Locale) : null;
        string? password = null;
        if (smtp is null || (smtp.PasswordProtected is { } p && (password = protector.TryUnprotect(p)) is null))
            return ServiceResult.Conflict(MessageRef.Of(MessageText.MailNotConfigured), "smtpNotConfigured");
        if (!gate.TryEnter(user.UserId))
            return ServiceResult.Conflict(MessageRef.Of(MessageText.SmtpTestTooSoon, (int)AlertLimits.TestGap.TotalSeconds), "smtpTestTooSoon");

        MailSendResult result;
        using (var timeout = new CancellationTokenSource(AlertLimits.SendTimeout, time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
            result = await mailer.SendAsync(smtp, password, AlertMailComposer.MailTest(smtp.MailLocale), linked.Token);
        await audit.TryWriteNowAsync(new AuditEntry("mailSettings.test", result.Ok ? AuditResult.Success : AuditResult.Failed,
            AuditTargets.MailSettings, MailSettings.Default, ReasonCode: result.Ok ? null : result.Stage,
            Summary: new AuditSummary().Add("sentTo", smtp.ToAddresses.Count).Add("serverCode", result.ServerCode).ToJson()), ct);
        return result.Ok
            ? ServiceResult.Success(new SmtpTestResponse(smtp.ToAddresses))
            : ServiceResult.Unprocessable(MessageRef.Of(MessageText.SmtpTestFailed), "smtpTestFailed",
                new Dictionary<string, object?> { ["stage"] = result.Stage, ["serverCode"] = result.ServerCode });
    }
}

/// <summary>寄信設定與告警通道共用的信箱格式檢查。</summary>
public static class MailAddressRules
{
    public static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>單一信箱、不帶顯示名稱；CRLF 等會讓解析後的地址與原字串不同，一律不收。</summary>
    public static bool IsEmail(string v) => MailAddress.TryCreate(v, out var a) && a.Address == v && a.DisplayName.Length == 0;
}
