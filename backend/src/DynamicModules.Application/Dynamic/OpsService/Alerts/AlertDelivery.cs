using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Ops;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.OpsService.Alerts;

/// <summary>領取到的一封信：寄送要用的設定與快照，<see cref="Attempts"/> 是領取後的次數。</summary>
public sealed record AlertClaim(long Id, AlertTransition Transition, AlertMail Mail, int Attempts, SmtpSettings Settings);

/// <summary>領取結果：<see cref="Claim"/> 為 null 且 <see cref="Lost"/> 為假＝沒有可寄的；<see cref="Lost"/>＝候選被別人領走（再領一次）。</summary>
public sealed record AlertClaimResult(AlertClaim? Claim, bool Lost = false);

/// <summary>
/// 第 1 步「領取」（維運設計 §17.2.6，短交易）：第一句更新通道列（<c>LastClaimUtc</c>）取得列鎖，之後依序做摘要信、額度、摘要化、領取，
/// 兩個寄送者不會一起看到「還差一封」而超額（O2-T31）。<paramref name="Fence"/> 是 <c>ops.monitor</c> 的租約檢查；
/// 正確性不靠租約（兩個條件式更新擋住重複領取，§17.7），測試直接模擬兩個寄送者時傳 null。
/// </summary>
public sealed record ClaimAlertRequest(JobLeaseFence? Fence, string ClaimedBy) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class ClaimAlertHandler(
    IScheduledJobRepository jobs, IAlertChannelRepository channels, IMailSettingsRepository mailSettings, IAlertOutboxRepository outbox,
    IOpsIncidentRepository incidents, IPolicyReader policies, IUnitOfWork uow, TimeProvider time, LocaleRegistry registry,
    Common.Localization.IMessageCatalog messages)
    : IRequestHandler<ClaimAlertRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ClaimAlertRequest r, CancellationToken ct)
    {
        if (r.Fence is { } fence && !await jobs.HoldsLeaseAsync(fence.JobKey, fence.Owner, fence.NowUtc, ct))
            return ServiceResult.Conflict(null, JobLeaseFence.LostCode);
        var now = time.GetUtcNow().UtcDateTime;
        // 第一句：通道列鎖。沒有通道列＝從沒設定過，佇列列寫入時都已標 skipped
        if (!await channels.LockAsync(AlertChannel.Smtp, now, ct)) return ServiceResult.Success(new AlertClaimResult(null));
        if (SmtpSettings.From(await mailSettings.GetAsync(ct), await channels.GetAsync(AlertChannel.Smtp, ct)) is not { } settings)
        {
            // 寫入後才被停用或清掉設定（通道或寄信設定）：還沒寄的一律略過
            await outbox.SkipPendingAsync(AlertChannel.Smtp, ct);
            return ServiceResult.Success(new AlertClaimResult(null));
        }

        var defaultLocale = await policies.ChoiceAsync(PolicyCatalog.LocaleDefault, null, ct);
        var mailContext = registry.ContextFor(settings.MailLocale ?? defaultLocale, defaultLocale);
        await DigestAsync(now, mailContext, ct);

        var policy = await AlertEmailPolicy.ReadAsync(policies, ct);
        var full = await outbox.CountQuotaUsedAsync(AlertChannel.Smtp, now - AlertLimits.QuotaWindow, ct) >= policy.MaxPerHour;
        // 已達上限：不再逐封寄，未領取的 pending 併進下一個整點的摘要信（同一把鎖內）
        if (full) await outbox.DigestPendingAsync(AlertChannel.Smtp, now, ct);
        if (await outbox.NextClaimableAsync(AlertChannel.Smtp, now, digestOnly: full, ct) is not { } next)
            return ServiceResult.Success(new AlertClaimResult(null));
        if (!await outbox.TryClaimAsync(next.Id, r.ClaimedBy, now, now + AlertLimits.ClaimTimeout, ct))
            return ServiceResult.Success(new AlertClaimResult(null, Lost: true));
        return ServiceResult.Success(new AlertClaimResult(
            new AlertClaim(next.Id, next.Transition, new AlertMail(next.Subject, next.Body), next.Attempts + 1, settings)));
    }

    /// <summary>
    /// 每個整點後的第一輪：這個整點之後還沒有摘要信、而有整點前建立且還沒掛上的 <c>digested</c> 列，就新增一封摘要信並把那些列掛上去（同一交易）。
    /// </summary>
    private async Task DigestAsync(DateTime now, LocaleContext mailContext, CancellationToken ct)
    {
        var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        if (await outbox.DigestExistsSinceAsync(AlertChannel.Smtp, hour, ct)) return;
        var rows = await outbox.ListUnattachedDigestedAsync(AlertChannel.Smtp, hour, ct);
        if (rows.Count == 0) return;
        var items = new List<DigestItem>(rows.Count);
        foreach (var row in rows)
        {
            var incident = row.IncidentId is { } id ? await incidents.GetAsync(id, ct) : null;
            items.Add(incident is null
                ? new DigestItem(row.IncidentId, IncidentSeverity.Warning, null, row.Subject, row.CreatedUtc)
                : new DigestItem(incident.Id, incident.Severity, incident.Category,
                    IncidentTitles.Text(messages, mailContext, incident.TitleKey, IncidentTitles.FromJson(incident.TitleParamsJson)),
                    row.CreatedUtc));
        }
        var mail = AlertMailComposer.Digest(items, messages, mailContext);
        var digest = new AlertOutbox
        {
            Channel = AlertChannel.Smtp, Transition = AlertTransition.Digest, Subject = mail.Subject, Body = mail.Body,
            CreatedUtc = now, NextAttemptUtc = now, Status = AlertOutboxStatus.Pending,
        };
        outbox.Add(digest);
        await uow.CommitAsync(ct);
        await outbox.AttachToDigestAsync([.. rows.Select(x => x.Id)], digest.Id, ct);
    }
}

/// <summary>
/// 第 3 步「記結果」（短交易）：<c>WHERE Id = ? AND ClaimedBy = 我</c>。成功記 <c>sent</c>，並把通道失敗事件結案（<c>recovered</c>）；
/// 失敗依重試表記 <c>pending</c> 與下次時間，第 5 次（或 <paramref name="GiveUp"/>）記 <c>failed</c> 並開或更新 <c>channel.smtp</c> 事件（只站內）。
/// 影響 0 列＝領取已過期、被別人接手，只記 Warning（這封可能重複寄出）。
/// </summary>
public sealed record CompleteAlertRequest(long Id, string ClaimedBy, int Attempts, MailSendResult Result, bool GiveUp = false)
    : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class CompleteAlertHandler(
    IAlertChannelRepository channels, IAlertOutboxRepository outbox, IncidentRecorder recorder, IOpsEnvironment env, IUnitOfWork uow,
    TimeProvider time, ILogger<CompleteAlertHandler> logger) : IRequestHandler<CompleteAlertRequest, ServiceResult>
{
    public const string ChannelCode = "channel.smtp";

    public static string ChannelFingerprint => ErrorFingerprint.ForInfra("infra", ChannelCode);

    public async Task<ServiceResult> Handle(CompleteAlertRequest r, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var final = !r.Result.Ok && (r.GiveUp || r.Attempts >= AlertLimits.MaxAttempts);
        var status = r.Result.Ok ? AlertOutboxStatus.Sent : final ? AlertOutboxStatus.Failed : AlertOutboxStatus.Pending;
        var error = r.Result.Ok ? null : r.Result.Describe();
        var next = r.Result.Ok || final ? now : AlertLimits.NextAttempt(now, r.Attempts);
        if (!await outbox.TryCompleteAsync(r.Id, r.ClaimedBy, status, next, error, r.Result.Ok ? now : null, ct))
        {
            logger.LogWarning("告警信 {OutboxId} 的領取已過期、被別人接手，結果沒記上（這封可能重複寄出）", r.Id);
            return ServiceResult.Success(false);
        }
        if (r.Result.Ok)
        {
            await channels.RecordSuccessAsync(AlertChannel.Smtp, now, ct);
            await recorder.RecordAsync(Signal(now, r.Result) with { Recovered = true }, ct);
        }
        else
        {
            await channels.RecordFailureAsync(AlertChannel.Smtp, now, error!, ct);
            if (final)
            {
                logger.LogWarning("告警信 {OutboxId} 第 {Attempts} 次仍寄不出去（{Error}），標失敗", r.Id, r.Attempts, error);
                await recorder.RecordAsync(Signal(now, r.Result), ct);
            }
        }
        await uow.CommitAsync(ct);
        return ServiceResult.Success(true);
    }

    /// <summary>通道失敗事件（§17.2.2 來源表 <c>channel</c>）：基礎設施、警告；永遠不寄信（§17.9）。</summary>
    private IncidentSignal Signal(DateTime now, MailSendResult result) => new()
    {
        Fingerprint = ChannelFingerprint, Source = IncidentSource.Channel, Category = IncidentCategory.Infra, Severity = IncidentSeverity.Warning,
        Code = ChannelCode, TitleKey = IncidentTitles.ChannelFailed, TitleParams = IncidentTitles.Channel(result.Stage ?? MailSendResult.Send, result.ServerCode),
        OccurredUtc = now, Version = env.Version,
    };
}

/// <summary>
/// 寄送佇列的寄送者（<c>ops.monitor</c> 每輪先跑它，§17.2.8）：一封一封「領取 → 交易外寄 SMTP → 記結果」，
/// 寄 SMTP 的那一步不在任何交易裡（不准持全庫寫入鎖等網路）；單封逾時 30 秒。密碼在寄送時才解，解不開當設定不完整，直接標失敗並開通道失敗事件。
/// </summary>
public sealed class AlertDispatcher(IAlertMailSender mailer, IAlertSecretProtector protector, OpsInstance instance, TimeProvider time,
    ILogger<AlertDispatcher> logger)
{
    /// <summary>連續被別人領走幾次就停這一輪（下一輪再來）。</summary>
    private const int MaxLostInARow = 3;

    public const string ConfigStage = "config";

    /// <summary>領取者：實例 id＋本輪執行 id（<c>ClaimedBy</c> 最長 100）。</summary>
    public string NewClaimer()
    {
        var id = $"{instance.InstanceId}:{Guid.NewGuid():N}";
        return id.Length <= 100 ? id : id[^100..];
    }

    /// <summary>送到沒有可寄的為止；回傳寄出（含失敗）的封數。</summary>
    public async Task<int> RunAsync(ISender sender, Func<JobLeaseFence?> fence, CancellationToken ct)
    {
        var claimer = NewClaimer();
        var handled = 0;
        var lost = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var claimed = await sender.Send(new ClaimAlertRequest(fence(), claimer), ct);
            if (claimed is { IsConflict: true, Code: JobLeaseFence.LostCode }) throw new JobLeaseLostException("ops.monitor");
            if (!claimed.IsSuccess) throw new InvalidOperationException($"告警信領取失敗：{claimed.Message}");
            var result = (AlertClaimResult)claimed.Data!;
            if (result.Lost)
            {
                if (++lost >= MaxLostInARow) return handled;
                continue;
            }
            if (result.Claim is not { } claim) return handled;
            lost = 0;
            var (sent, giveUp) = await SendAsync(claim, ct);
            await sender.Send(new CompleteAlertRequest(claim.Id, claimer, claim.Attempts, sent, giveUp), ct);
            handled++;
        }
    }

    /// <summary>交易外寄一封；不丟例外（失敗轉成階段）。</summary>
    public async Task<(MailSendResult Result, bool GiveUp)> SendAsync(AlertClaim claim, CancellationToken ct)
    {
        string? password = null;
        if (claim.Settings.PasswordProtected is { } protectedText && (password = protector.TryUnprotect(protectedText)) is null)
        {
            logger.LogError("告警信 {OutboxId} 寄不出去：SMTP 密碼解不開（金鑰可能不見了），請到寄信設定重填", claim.Id);
            return (new MailSendResult(false, ConfigStage), true);
        }
        try
        {
            using var timeout = new CancellationTokenSource(AlertLimits.SendTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            return (await mailer.SendAsync(claim.Settings, password, claim.Mail, linked.Token), false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "告警信 {OutboxId} 寄送丟例外", claim.Id);
            return (new MailSendResult(false, MailSendResult.Send), false);
        }
    }
}
