using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.OpsService.Monitor;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;

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
/// 補寫時：直送已寄出的記 <c>sentDirect</c>（不再寄）、已過時的記 <c>skipped</c>，其餘 <c>pending</c>；
/// 不是 <c>sentDirect</c> 的，提交後從直送佇列撤回（之後交給佇列，不會兩邊都寄）。
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
            Subject = mail.Subject, Body = mail.Body, CreatedUtc = now, NextAttemptUtc = now, Status = AlertOutboxStatus.Pending,
        };
        if (SmtpSettings.From(channel) is null) row.Status = AlertOutboxStatus.Skipped;
        else if (backfill is not null && direct.SentUtc(backfill.TransitionId) is { } sentAt)
        {
            row.Status = AlertOutboxStatus.SentDirect;
            row.SentUtc = sentAt;
        }
        else if (backfill is { Superseded: true }) row.Status = AlertOutboxStatus.Skipped;
        if (backfill is not null && row.Status != AlertOutboxStatus.SentDirect)
            afterCommit.Add("撤回直送", () => direct.Withdraw(backfill.TransitionId));
        outbox.Add(row);
        return row.Status;
    }
}
