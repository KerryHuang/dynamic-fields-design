using System.Collections.Concurrent;
using System.Net.Mail;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.OpsService.Monitor;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.OpsService.Alerts;

// 維運設計 §17.5 的 SMTP 通道 API：讀設定、存設定、寄測試信。全部只給系統管理員；開 Demo 登入時存設定與測試信回 403（O2-U10）。

public sealed record SmtpChannelResponse(
    bool Enabled, string? Host, int Port, string Security, string? UserName, bool HasPassword, bool PasswordUnreadable,
    string? FromAddress, string? FromName, IReadOnlyList<string> ToAddresses, Setting<string> MailLocale,
    DateTime? LastSuccessUtc, DateTime? LastFailureUtc, string? LastError, int ConsecutiveFailures, bool Editable);

public sealed record SmtpTestResponse(IReadOnlyList<string> SentTo);

public sealed record GetSmtpChannelRequest : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed class GetSmtpChannelHandler(IAlertChannelRepository channels, IAlertSecretProtector protector, AuthSettings auth)
    : IRequestHandler<GetSmtpChannelRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetSmtpChannelRequest request, CancellationToken ct)
    {
        var c = await channels.GetAsync(AlertChannel.Smtp, ct) ?? new AlertChannel();
        var hasPassword = c.PasswordProtected is not null;
        return ServiceResult.Success(new SmtpChannelResponse(
            c.Enabled, c.Host, c.Port, OpsMonitorReader.Camel(c.Security), c.UserName, hasPassword,
            // 金鑰不見（PostgreSQL 沒設 KeysDirectory 而重啟）時解不開：畫面要求重填（§17.2.6）
            hasPassword && protector.TryUnprotect(c.PasswordProtected!) is null,
            c.FromAddress, c.FromName, AlertAddresses.Split(c.ToAddresses),
            new Setting<string>(AlertMailText.Locale(c.MailLocale), AlertMailText.FactoryLocale, c.MailLocale is null),
            c.LastSuccessUtc, c.LastFailureUtc, c.LastError, c.ConsecutiveFailures, !auth.DemoLoginEnabled));
    }
}

/// <summary>
/// 存 SMTP 設定（§17.5 PUT）。<paramref name="Password"/>：null＝不變、空字串＝清除、其他＝新密碼（Data Protection 加密後存，不存明文）。
/// <paramref name="MailLocaleSet"/> 為假＝信件語系不變；為真而 <paramref name="MailLocale"/> 為 null＝回到出廠。
/// </summary>
public sealed record UpdateSmtpChannelRequest(
    bool Enabled, string? Host, int Port, string? Security, string? UserName, string? Password, string? FromAddress, string? FromName,
    IReadOnlyList<string>? ToAddresses, bool MailLocaleSet, string? MailLocale)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => new("ops.channel.update", AuditTargets.Channel, AlertChannel.Smtp);
}

public sealed class UpdateSmtpChannelHandler(
    IAlertChannelRepository channels, IAlertSecretProtector protector, AuthSettings auth, ICurrentUser user, AuditContext audit, IUnitOfWork uow,
    TimeProvider time) : IRequestHandler<UpdateSmtpChannelRequest, ServiceResult>
{
    // 欄位長度＝§17.4 的欄位長度
    private const int HostMax = 255, AddressMax = 255, NameMax = 100, ToJoinedMax = 2000;

    public async Task<ServiceResult> Handle(UpdateSmtpChannelRequest r, CancellationToken ct)
    {
        // 公開 Demo 人人是管理員，可以把主機設成任意位址、讓伺服器替他連內網或寄信（§17.2.6、O2-U10）
        if (auth.DemoLoginEnabled) return ServiceResult.Forbidden();

        var errors = new Dictionary<string, MessageRef[]>();
        void Error(string key, MessageRef m) => errors.TryAdd(key, [m]);
        var host = Blank(r.Host);
        var from = Blank(r.FromAddress);
        var to = (r.ToAddresses ?? []).Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        var security = r.Security switch
        {
            "none" => AlertSecurity.None, "startTls" => AlertSecurity.StartTls, "sslOnConnect" => AlertSecurity.SslOnConnect, _ => (AlertSecurity?)null,
        };
        if (security is null) Error("security", MessageRef.Of(MessageText.OptionInvalid));
        if (host is null && r.Enabled) Error("host", MessageRef.Of(MessageText.Required));
        if (host is { Length: > HostMax }) Error("host", MessageRef.Of(MessageText.TooLong, HostMax));
        if (r.Port is < 1 or > 65535) Error("port", MessageRef.Of(MessageText.HealthCheckOutOfRange, 1, 65535));
        if (from is null && r.Enabled) Error("fromAddress", MessageRef.Of(MessageText.Required));
        if (from is not null && (from.Length > AddressMax || !IsEmail(from))) Error("fromAddress", MessageRef.Of(MessageText.EmailFormat));
        if (Blank(r.FromName) is { Length: > NameMax }) Error("fromName", MessageRef.Of(MessageText.TooLong, NameMax));
        if (Blank(r.UserName) is { Length: > AddressMax }) Error("userName", MessageRef.Of(MessageText.TooLong, AddressMax));
        if ((to.Count == 0 && r.Enabled) || to.Count > AlertLimits.MaxRecipients)
            Error("toAddresses", MessageRef.Of(MessageText.SmtpRecipientsCount, 1, AlertLimits.MaxRecipients));
        else if (to.Any(a => a.Length > AddressMax || !IsEmail(a))) Error("toAddresses", MessageRef.Of(MessageText.EmailFormat));
        else if (string.Join(",", to).Length > ToJoinedMax) Error("toAddresses", MessageRef.Of(MessageText.TooLong, ToJoinedMax));
        if (r.MailLocaleSet && r.MailLocale is not null && !AlertMailText.Locales.Contains(r.MailLocale))
            Error("mailLocale", MessageRef.Of(MessageText.OptionInvalid));
        if (errors.Count > 0) return ServiceResult.Invalid(errors);

        var row = await channels.GetTrackedAsync(AlertChannel.Smtp, ct);
        if (row is null)
        {
            row = new AlertChannel();
            channels.Add(row);
        }
        // 不准明文傳密碼：加密方式 none 只給以來源位址授權、不需登入的內網 relay（§17.2.6）
        var keepsPassword = r.Password is null ? row.PasswordProtected is not null : r.Password.Length > 0;
        if (security == AlertSecurity.None && (Blank(r.UserName) is not null || keepsPassword))
            return ServiceResult.Invalid("security", MessageRef.Of(MessageText.SmtpPlaintextAuth), "smtpPlaintextAuth");

        var before = Snapshot(row);
        row.Enabled = r.Enabled;
        row.Host = host;
        row.Port = r.Port;
        row.Security = security!.Value;
        row.UserName = Blank(r.UserName);
        if (r.Password is not null) row.PasswordProtected = r.Password.Length == 0 ? null : protector.Protect(r.Password);
        row.FromAddress = from;
        row.FromName = Blank(r.FromName);
        row.ToAddresses = to.Count == 0 ? null : string.Join(",", to);
        if (r.MailLocaleSet) row.MailLocale = r.MailLocale;
        row.ModifiedUserId = user.UserId;
        row.ModifiedUtc = time.GetUtcNow().UtcDateTime;
        await uow.CommitAsync(ct);
        audit.Describe(before, Snapshot(row));
        if (r.Password is { Length: > 0 }) audit.Detail("passwordChanged", true);
        return ServiceResult.Success();
    }

    /// <summary>稽核差異：密碼只記有沒有，不記內容。</summary>
    private static object Snapshot(AlertChannel c) => new
    {
        c.Enabled, c.Host, c.Port, Security = OpsMonitorReader.Camel(c.Security), c.UserName, HasPassword = c.PasswordProtected is not null,
        c.FromAddress, c.FromName, c.ToAddresses, c.MailLocale,
    };

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static bool IsEmail(string v) => MailAddress.TryCreate(v, out var a) && a.Address == v && a.DisplayName.Length == 0;
}

/// <summary>測試信的節流：同一管理員 30 秒一次（只在本實例記憶體；技術限制，§17.9）。</summary>
public sealed class SmtpTestGate(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);

    public bool TryEnter(string userId)
    {
        var now = time.GetUtcNow();
        while (true)
        {
            if (_last.TryGetValue(userId, out var last))
            {
                if (now - last < AlertLimits.TestGap) return false;
                if (_last.TryUpdate(userId, now, last)) return true;
            }
            else if (_last.TryAdd(userId, now)) return true;
        }
    }
}

/// <summary>
/// 寄測試信（§17.2.6「測試信」）：不經佇列、立刻用<b>已儲存</b>的設定寄到清單上的全部收件人（不能臨時填地址），30 秒逾時、通道的信件語系。
/// 不是交易型請求（寄 SMTP 不在交易裡）；稽核 <c>ops.channel.test</c> 在交易外寫（寫不進只記 log）。失敗回 422 與階段、回應碼，不回例外全文。
/// </summary>
public sealed record SendSmtpTestRequest : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed class SendSmtpTestHandler(
    IAlertChannelRepository channels, IAlertSecretProtector protector, IAlertMailSender mailer, SmtpTestGate gate, AuthSettings auth,
    ICurrentUser user, IAuditLog audit, TimeProvider time) : IRequestHandler<SendSmtpTestRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SendSmtpTestRequest request, CancellationToken ct)
    {
        if (auth.DemoLoginEnabled) return ServiceResult.Forbidden();
        var settings = SmtpSettings.From(await channels.GetAsync(AlertChannel.Smtp, ct), requireEnabled: false);
        string? password = null;
        if (settings is null || (settings.PasswordProtected is { } p && (password = protector.TryUnprotect(p)) is null))
            return ServiceResult.Conflict(MessageRef.Of(MessageText.SmtpNotConfigured), "smtpNotConfigured");
        if (!gate.TryEnter(user.UserId))
            return ServiceResult.Conflict(MessageRef.Of(MessageText.SmtpTestTooSoon, (int)AlertLimits.TestGap.TotalSeconds), "smtpTestTooSoon");

        MailSendResult result;
        using (var timeout = new CancellationTokenSource(AlertLimits.SendTimeout, time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
            result = await mailer.SendAsync(settings, password, AlertMailComposer.Test(settings.MailLocale), linked.Token);
        await audit.TryWriteNowAsync(new AuditEntry("ops.channel.test", result.Ok ? AuditResult.Success : AuditResult.Failed,
            AuditTargets.Channel, AlertChannel.Smtp, ReasonCode: result.Ok ? null : result.Stage,
            Summary: new AuditSummary().Add("sentTo", settings.ToAddresses.Count).Add("serverCode", result.ServerCode).ToJson()), ct);
        return result.Ok
            ? ServiceResult.Success(new SmtpTestResponse(settings.ToAddresses))
            : ServiceResult.Unprocessable(MessageRef.Of(MessageText.SmtpTestFailed), "smtpTestFailed",
                new Dictionary<string, object?> { ["stage"] = result.Stage, ["serverCode"] = result.ServerCode });
    }
}
