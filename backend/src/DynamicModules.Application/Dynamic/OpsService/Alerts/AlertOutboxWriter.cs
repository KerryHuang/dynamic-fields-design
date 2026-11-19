using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.OpsService.Monitor;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.OpsService.Alerts;

/// <summary>寄信的三項政策（§17.6.1）：哪些類別、最低嚴重度、每小時上限。</summary>
public sealed record AlertEmailPolicy(IReadOnlyList<string> Categories, IncidentSeverity MinSeverity, int MaxPerHour)
{
    public static AlertEmailPolicy Factory { get; } = new(
        PolicyDefinition.RecipientItems(PolicyCatalog.OpsAlertEmailCategories.Factory), Severity(PolicyCatalog.OpsAlertEmailMinSeverity.Factory),
        int.Parse(PolicyCatalog.OpsAlertEmailMaxPerHour.Factory, System.Globalization.CultureInfo.InvariantCulture));

    public static async Task<AlertEmailPolicy> ReadAsync(IPolicyReader policies, CancellationToken ct) => new(
        await policies.ListAsync(PolicyCatalog.OpsAlertEmailCategories, null, ct),
        Severity(await policies.ChoiceAsync(PolicyCatalog.OpsAlertEmailMinSeverity, null, ct)),
        await policies.IntAsync(PolicyCatalog.OpsAlertEmailMaxPerHour, null, ct));

    /// <summary>資訊級一律不寄；其他看類別與最低嚴重度。</summary>
    public bool Allows(IncidentCategory category, IncidentSeverity severity) =>
        severity != IncidentSeverity.Info && severity >= MinSeverity && Categories.Contains(OpsMonitorReader.Camel(category), StringComparer.Ordinal);

    private static IncidentSeverity Severity(string choice) => choice == "critical" ? IncidentSeverity.Critical : IncidentSeverity.Warning;
}

/// <summary>寫佇列列時，健康檢查補寫帶來的資訊（§17.2.6「資料庫寫不進時的直送」最後一條）。</summary>
/// <param name="TransitionId">健康檢查轉換的穩定識別碼，對照直送紀錄。</param>
/// <param name="Superseded">同一檢查在補寫佇列後面已經結案：這封開啟或升級信已經過時。</param>
public sealed record AlertBackfill(string TransitionId, bool Superseded);

/// <summary>
/// 事件轉換時寫一列告警寄送佇列（維運設計 §17.2.6「寄送」）：和事件轉換、站內通知在同一個交易；主旨與內文以通道的信件語系組好存成快照。
/// 通道失敗事件永遠不寄（防自己觸發自己，§17.9）；不在寄信類別或低於最低嚴重度的不寫；通道停用或沒設定照寫、標 <c>skipped</c>。
/// 補寫時先向直送佇列保留這個轉換（<see cref="DirectAlertQueue.Reserve"/>，§17.13 審查修正）：直送已寄出的記 <c>sentDirect</c>；
/// SMTP 在途的記成由直送領著的 <c>sending</c>（寄完由直送記結果）；其餘照寫（已過時的 <c>skipped</c>、否則 <c>pending</c>），直送不再寄。提交後確認交接。
/// </summary>
public sealed class AlertOutboxWriter(
    IAlertChannelRepository channels, IAlertOutboxRepository outbox, IOpsIncidentRepository incidents, IPolicyReader policies,
    DirectAlertQueue direct, AfterCommitActions afterCommit, IOpsEnvironment env, AlertMailOptions options, TimeProvider time)
{
    /// <summary>回傳這列的狀態；沒寫回 null。</summary>
    public async Task<AlertOutboxStatus?> EnqueueAsync(long incidentId, AlertTransition transition, AlertBackfill? backfill, CancellationToken ct)
    {
        if (await incidents.GetAsync(incidentId, ct) is not { } incident || incident.Source == IncidentSource.Channel) return null;
        if (!(await AlertEmailPolicy.ReadAsync(policies, ct)).Allows(incident.Category, incident.Severity)) return null;
        var channel = await channels.GetAsync(AlertChannel.Smtp, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var mail = AlertMailComposer.Incident(incident, transition, channel?.MailLocale, env.Edition, options);
        var row = new AlertOutbox
        {
            Channel = AlertChannel.Smtp, IncidentId = incident.Id, Transition = transition, TransitionId = backfill?.TransitionId,
            Subject = mail.Subject, Body = mail.Body, CreatedUtc = now, NextAttemptUtc = now,
            Status = SmtpSettings.From(channel) is null || backfill is { Superseded: true } ? AlertOutboxStatus.Skipped : AlertOutboxStatus.Pending,
        };
        if (backfill is not null)
        {
            var reserved = direct.Reserve(backfill.TransitionId, row.Status);
            switch (reserved.Kind)
            {
                case DirectHandoff.Sent:
                    row.Status = AlertOutboxStatus.SentDirect;
                    row.SentUtc = reserved.SentUtc;
                    break;
                case DirectHandoff.InFlight:
                    row.Status = AlertOutboxStatus.Sending;
                    row.ClaimedBy = DirectAlertQueue.ClaimerFor(backfill.TransitionId);
                    row.ClaimedUntilUtc = now + DirectAlertQueue.HandoffClaim;
                    break;
            }
            if (reserved.Kind != DirectHandoff.None) afterCommit.Add("直送交接", () => direct.Confirm(backfill.TransitionId));
        }
        outbox.Add(row);
        return row.Status;
    }

    /// <summary>
    /// 補寫去重返回（結果列已在，§17.13 審查修正必修 2）：上一次其實已提交、執行器卻以為沒寫入而交給了直送。核對持久化的佇列列完成交接：
    /// 直送已寄出的，還在 <c>pending</c> 的列記 <c>sentDirect</c>；在途的，還在 <c>pending</c> 的列改由直送領著；還沒開始寄的不再寄。提交後確認交接。
    /// </summary>
    public async Task ReconcileBackfillAsync(string transitionId, CancellationToken ct)
    {
        var reserved = direct.Reserve(transitionId, null);
        if (reserved.Kind == DirectHandoff.None) return;
        // 沒有佇列列（政策不寄）或佇列已經領走時，兩個條件式更新都影響 0 列
        if (reserved is { Kind: DirectHandoff.Sent, SentUtc: { } sentAt }) await outbox.MarkSentDirectAsync(transitionId, sentAt, ct);
        else if (reserved.Kind == DirectHandoff.InFlight)
            await outbox.ClaimForDirectAsync(transitionId, DirectAlertQueue.ClaimerFor(transitionId), time.GetUtcNow().UtcDateTime + DirectAlertQueue.HandoffClaim, ct);
        afterCommit.Add("直送交接", () => direct.Confirm(transitionId));
    }
}

/// <summary>直送寄完（或在途交接後寄失敗）：把直送領著的佇列列記成 <c>sentDirect</c> 或交回（短交易，§17.13 審查修正）。回傳有沒有更新到。</summary>
public sealed record ReleaseDirectAlertRequest(string TransitionId, DateTime? SentUtc, AlertOutboxStatus Fallback) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class ReleaseDirectAlertHandler(IAlertOutboxRepository outbox, TimeProvider time) : IRequestHandler<ReleaseDirectAlertRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ReleaseDirectAlertRequest r, CancellationToken ct) => ServiceResult.Success(
        await outbox.ReleaseDirectAsync(r.TransitionId, DirectAlertQueue.ClaimerFor(r.TransitionId),
            r.SentUtc is null ? r.Fallback : AlertOutboxStatus.SentDirect, r.SentUtc, time.GetUtcNow().UtcDateTime, ct));
}
