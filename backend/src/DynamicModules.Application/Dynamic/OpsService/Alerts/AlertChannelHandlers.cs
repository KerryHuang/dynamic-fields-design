using System.Collections.Concurrent;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.MailService;
using DynamicModules.Application.Dynamic.OpsService.Monitor;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.OpsService.Alerts;

// 維運設計 §17.5 的 SMTP 告警通道 API：讀設定、存設定、寄測試信。全部只給系統管理員；開 Demo 登入時存設定與測試信回 403（O2-U10）。
// SMTP 連線與寄件人在核心寄信設定（MailService，帳號信共用）；通道只管啟用、收件信箱與信件語系。

public sealed record SmtpChannelResponse(
    bool Enabled, IReadOnlyList<string> ToAddresses, Setting<string> MailLocale,
    DateTime? LastSuccessUtc, DateTime? LastFailureUtc, string? LastError, int ConsecutiveFailures, bool Editable, bool MailConfigured);

public sealed record SmtpTestResponse(IReadOnlyList<string> SentTo);

public sealed record GetSmtpChannelRequest : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed class GetSmtpChannelHandler(IAlertChannelRepository channels, IMailSettingsRepository mailSettings, AuthSettings auth)
    : IRequestHandler<GetSmtpChannelRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetSmtpChannelRequest request, CancellationToken ct)
    {
        var c = await channels.GetAsync(AlertChannel.Smtp, ct) ?? new AlertChannel();
        return ServiceResult.Success(new SmtpChannelResponse(
            c.Enabled, AlertAddresses.Split(c.ToAddresses),
            new Setting<string>(AlertMailText.Locale(c.MailLocale), AlertMailText.FactoryLocale, c.MailLocale is null),
            c.LastSuccessUtc, c.LastFailureUtc, c.LastError, c.ConsecutiveFailures, !auth.DemoLoginEnabled,
            (await mailSettings.GetAsync(ct))?.Configured ?? false));
    }
}

/// <summary>
/// 存告警通道設定（§17.5 PUT）：啟用、收件信箱（啟用時 1–20 個）、信件語系。寄信設定還沒設定好也可以存（畫面提示先去寄信設定）。
/// <paramref name="MailLocaleSet"/> 為假＝信件語系不變；為真而 <paramref name="MailLocale"/> 為 null＝回到出廠。
/// </summary>
public sealed record UpdateSmtpChannelRequest(bool Enabled, IReadOnlyList<string>? ToAddresses, bool MailLocaleSet, string? MailLocale)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => new("ops.channel.update", AuditTargets.Channel, AlertChannel.Smtp);
}

public sealed class UpdateSmtpChannelHandler(
    IAlertChannelRepository channels, AuthSettings auth, ICurrentUser user, AuditContext audit, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<UpdateSmtpChannelRequest, ServiceResult>
{
    // 欄位長度＝§17.4 的欄位長度
    private const int AddressMax = 255, ToJoinedMax = 2000;

    public async Task<ServiceResult> Handle(UpdateSmtpChannelRequest r, CancellationToken ct)
    {
        if (auth.DemoLoginEnabled) return ServiceResult.Forbidden();

        var errors = new Dictionary<string, MessageRef[]>();
        void Error(string key, MessageRef m) => errors.TryAdd(key, [m]);
        // JSON 陣列可能帶 null 元素（契約是字串陣列）：正規化前擋下，回欄位錯誤而不是 500（審查修正必修 3）
        var nullRecipient = r.ToAddresses?.Any(a => a is null) == true;
        var to = (r.ToAddresses ?? []).Where(a => a is not null).Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        if (nullRecipient) Error("toAddresses", MessageRef.Of(MessageText.EmailFormat));
        else if ((to.Count == 0 && r.Enabled) || to.Count > AlertLimits.MaxRecipients)
            Error("toAddresses", MessageRef.Of(MessageText.SmtpRecipientsCount, 1, AlertLimits.MaxRecipients));
        else if (to.Any(a => a.Length > AddressMax || !MailAddressRules.IsEmail(a))) Error("toAddresses", MessageRef.Of(MessageText.EmailFormat));
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
        var before = Snapshot(row);
        row.Enabled = r.Enabled;
        row.ToAddresses = to.Count == 0 ? null : string.Join(",", to);
        if (r.MailLocaleSet) row.MailLocale = r.MailLocale;
        row.ModifiedUserId = user.UserId;
        row.ModifiedUtc = time.GetUtcNow().UtcDateTime;
        await uow.CommitAsync(ct);
        audit.Describe(before, Snapshot(row));
        return ServiceResult.Success();
    }

    private static object Snapshot(AlertChannel c) => new { c.Enabled, c.ToAddresses, c.MailLocale };
}

/// <summary>測試信的節流：同一管理員 30 秒一次（告警通道與寄信設定的測試信共用；只在本實例記憶體；技術限制，§17.9）。</summary>
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
/// 寄測試信（§17.2.6「測試信」）：不經佇列、立刻用<b>已儲存</b>的寄信設定寄到通道清單上的全部收件人（不能臨時填地址），30 秒逾時、通道的信件語系。
/// 寄信設定不完整、密碼解不開或沒有收件人回 409 <c>smtpNotConfigured</c>。
/// 不是交易型請求（寄 SMTP 不在交易裡）；稽核 <c>ops.channel.test</c> 在交易外寫（寫不進只記 log）。失敗回 422 與階段、回應碼，不回例外全文。
/// </summary>
public sealed record SendSmtpTestRequest : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed class SendSmtpTestHandler(
    IAlertChannelRepository channels, IMailSettingsRepository mailSettings, IAlertSecretProtector protector, IAlertMailSender mailer,
    SmtpTestGate gate, AuthSettings auth, ICurrentUser user, IAuditLog audit, TimeProvider time) : IRequestHandler<SendSmtpTestRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SendSmtpTestRequest request, CancellationToken ct)
    {
        if (auth.DemoLoginEnabled) return ServiceResult.Forbidden();
        var settings = SmtpSettings.From(await mailSettings.GetAsync(ct), await channels.GetAsync(AlertChannel.Smtp, ct), requireEnabled: false);
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
