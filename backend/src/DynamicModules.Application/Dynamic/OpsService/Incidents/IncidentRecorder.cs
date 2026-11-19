using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Ops;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.OpsService.Incidents;

/// <summary>
/// 事件記錄器（維運設計 §17.2.2「轉換與通知」）：只能在呼叫端的交易裡用（交易型請求的 handler 內）。
/// 同指紋沒有列→新增「開啟」；有列且未結→次數＋n、嚴重度依規則升降；有列且已結→重開並標回歸。
/// 全部是條件式更新（<c>WHERE Status = 讀到的狀態</c>），影響 0 列或新增撞唯一鍵就重讀再判斷，最多三次（§17.7）；
/// 順序是「先 ExecuteUpdate、後 SaveChanges 新增」，撞鍵是 SaveChanges 的錯誤、有 savepoint 保護。
/// 通知與事件同一交易，交易失敗兩者都不會出現。
/// </summary>
public sealed class IncidentRecorder(
    IOpsIncidentRepository incidents, OpsNotifier notifier, IUnitOfWork uow, IDatabaseErrors errors, TimeProvider time, ILogger<IncidentRecorder> logger)
{
    /// <summary>條件式更新的重試次數（技術限制，§17.9）。</summary>
    public const int MaxAttempts = 3;

    public async Task<(IncidentTransition Transition, long? IncidentId)> RecordAsync(IncidentSignal s, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var row = await incidents.FindByFingerprintAsync(s.Fingerprint, ct);
            // 健康檢查補寫的舊結果：事件已有更晚的轉換（別台已寫入較新的結果）就只留歷史，不把它當成新的回歸（§17.2.4 第 2 步）
            if (row is not null && s.Source == IncidentSource.Health && (row.LastSeenUtc > s.OccurredUtc || row.ResolvedUtc > s.OccurredUtc))
                return (IncidentTransition.History, row.Id);

            if (s.Recovered)
            {
                if (row is null || row.Status == IncidentStatus.Resolved) return (IncidentTransition.None, row?.Id);
                if (await incidents.TryResolveAsync(row.Id, row.Status, IncidentResolvedReason.Recovered, null, s.OccurredUtc, ct))
                    return (IncidentTransition.Resolved, row.Id);
                continue;
            }

            var bump = new IncidentBump(s.Count, s.OccurredUtc, s.Version, s.RequestId, s.Severity, IncidentTitles.ToJson(s.TitleParams));
            IncidentTransition transition;
            long id;
            if (row is null)
            {
                var created = New(s);
                incidents.Add(created);
                try
                {
                    await uow.CommitAsync(ct);
                }
                catch (Exception e) when (errors.IsUniqueViolation(e))
                {
                    // 別的實例剛新增同一個指紋：拿掉這列、重讀後改走更新
                    incidents.Detach(created);
                    continue;
                }
                (transition, id) = (IncidentTransition.Opened, created.Id);
            }
            else if (row.Status == IncidentStatus.Resolved)
            {
                if (!await incidents.TryRegressAsync(row.Id, bump, ct)) continue;
                (transition, id) = (IncidentTransition.Regressed, row.Id);
            }
            else
            {
                // 嚴重度只有健康檢查類會變（§17.2.2）；升級時退回開啟並再通知，降級只改欄位
                var severity = s.Source == IncidentSource.Health ? s.Severity : row.Severity;
                var escalate = s.Source == IncidentSource.Health && s.Severity > row.Severity;
                if (!await incidents.TryBumpAsync(row.Id, row.Status, bump with { Severity = severity }, escalate, ct)) continue;
                (transition, id) = (escalate ? IncidentTransition.Escalated : IncidentTransition.Updated, row.Id);
            }

            if (s.Sample is { } sample)
            {
                incidents.AddSample(ToSample(id, sample));
                await uow.CommitAsync(ct);
                await incidents.TrimSamplesAsync(id, OpsIncidentSample.MaxPerIncident, ct);
            }
            if (ShouldNotify(s, transition))
            {
                await notifier.NotifyAsync(id, s.TitleKey, s.TitleParams, s.Link, ct);
                await incidents.MarkNotifiedAsync(id, time.GetUtcNow().UtcDateTime, ct);
                await uow.CommitAsync(ct);
            }
            return (transition, id);
        }
        logger.LogWarning("維運事件 {Fingerprint} 的條件式更新重試 {Attempts} 次仍落空", s.Fingerprint, MaxAttempts);
        throw new InvalidOperationException($"維運事件 {s.Fingerprint} 並行更新重試 {MaxAttempts} 次仍落空");
    }

    /// <summary>新開（警告以上）、回歸、升級會通知；安全類每次發生都通知（O2-U11）；資訊級一律不通知。</summary>
    private static bool ShouldNotify(IncidentSignal s, IncidentTransition t)
    {
        if (s.Severity == IncidentSeverity.Info) return false;
        if (s.Source == IncidentSource.Security) return t is IncidentTransition.Opened or IncidentTransition.Regressed or IncidentTransition.Updated;
        return t is IncidentTransition.Opened or IncidentTransition.Regressed or IncidentTransition.Escalated;
    }

    private static OpsIncident New(IncidentSignal s) => new()
    {
        Fingerprint = s.Fingerprint, FingerprintVersion = ErrorFingerprint.Version, Source = s.Source, Category = s.Category,
        Severity = s.Severity, Status = IncidentStatus.Open, Code = Cut(s.Code, 100)!, TitleKey = s.TitleKey,
        TitleParamsJson = Cut(IncidentTitles.ToJson(s.TitleParams), OpsIncident.TitleParamsMax),
        NormalizedMessage = Cut(s.NormalizedMessage, OpsIncident.NormalizedMessageMax),
        ModuleKey = Cut(s.ModuleKey, 100), RuleKind = s.RuleKind, RuleId = s.RuleId, Route = Cut(s.Route, 200),
        OccurrenceCount = s.Count, FirstSeenUtc = s.OccurredUtc, LastSeenUtc = s.OccurredUtc,
        FirstVersion = Cut(s.Version, 50)!, LastVersion = Cut(s.Version, 50)!, LastRequestId = Cut(s.RequestId, 32),
    };

    private static OpsIncidentSample ToSample(long incidentId, IncidentSampleData d) => new()
    {
        IncidentId = incidentId, OccurredUtc = d.OccurredUtc, RequestId = Cut(d.RequestId, 32), UserId = Cut(d.UserId, 50), Version = Cut(d.Version, 50),
        Route = Cut(d.Route, 200), AuditLogId = d.AuditLogId, RawMessage = Cut(d.RawMessage, OpsIncidentSample.RawMessageMax),
        RawStack = Cut(d.RawStack, OpsIncidentSample.RawStackMax), Browser = Cut(d.Browser, 200), Locale = Cut(d.Locale, 20),
    };

    private static string? Cut(string? v, int max) => v is null ? null : v.Length > max ? v[..max] : v;
}
