using System.Collections.Concurrent;
using System.Globalization;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.OpsService.Monitor;

/// <summary>PATCH 語意的欄位：IsSet 為假＝省略（不變），IsSet 為真而 Value 為 null＝回到出廠值。</summary>
public readonly record struct Optional<T>(bool IsSet, T? Value) where T : struct
{
    public static readonly Optional<T> Unset = new(false, null);
}

/// <summary>改健康檢查的開關、間隔、門檻（§17.5 PUT；null＝回到出廠值，欄位省略＝不變）。</summary>
public sealed record UpdateHealthCheckRequest(string Code, Optional<bool> Enabled, Optional<int> IntervalMinutes, Optional<long> Warn, Optional<long> Critical)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => new("healthCheck.update", AuditTargets.HealthCheck, Code);
}

public sealed class UpdateHealthCheckHandler(IHealthCheckRepository checks, ICurrentUser user, AuditContext audit, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<UpdateHealthCheckRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateHealthCheckRequest r, CancellationToken ct)
    {
        if (HealthCheckCatalog.Find(r.Code) is not { } def) return ServiceResult.NotFound(MessageRef.Of(MessageText.HealthCheckNotFound));
        var errors = new Dictionary<string, MessageRef[]>();
        void Error(string key, MessageRef m) => errors[key] = [m];
        if (r.Enabled is { IsSet: true, Value: false } && !def.CanDisable) Error("enabled", MessageRef.Of(MessageText.HealthCheckCannotDisable));
        if (r.IntervalMinutes is { IsSet: true, Value: { } iv } && (iv < def.IntervalMin || iv > def.IntervalMax))
            Error("intervalMinutes", MessageRef.Of(MessageText.HealthCheckOutOfRange, def.IntervalMin, def.IntervalMax));
        CheckThreshold("warn", r.Warn, def.Warn, Error);
        CheckThreshold("critical", r.Critical, def.Critical, Error);
        if (errors.Count > 0) return ServiceResult.Invalid(errors);

        var row = await checks.GetTrackedAsync(r.Code, ct);
        if (row is null)
        {
            row = new HealthCheck { Code = r.Code, NextDueUtc = time.GetUtcNow().UtcDateTime };
            checks.Add(row);
        }
        var before = Snapshot(row);
        if (r.Enabled.IsSet) row.Enabled = r.Enabled.Value;
        if (r.IntervalMinutes.IsSet) row.IntervalMinutes = r.IntervalMinutes.Value;
        if (r.Warn.IsSet) row.WarnThreshold = r.Warn.Value;
        if (r.Critical.IsSet) row.CriticalThreshold = r.Critical.Value;
        // 門檻先後用套用後的生效值判斷：「越大越糟」警告 ≤ 嚴重，「越小越糟」相反
        var s = EffectiveHealthSettings.Of(def, row);
        if (s.Warn is { } w && s.Critical is { } c
            && (def.Direction == HealthDirection.HigherIsWorse ? w > c : def.Direction == HealthDirection.LowerIsWorse && w < c))
            return ServiceResult.Invalid("warn", MessageRef.Of(MessageText.HealthCheckThresholdOrder));
        row.ModifiedUserId = user.UserId;
        row.ModifiedUtc = time.GetUtcNow().UtcDateTime;
        await uow.CommitAsync(ct);
        audit.Describe(before, Snapshot(row));
        return ServiceResult.Success();
    }

    private static object Snapshot(HealthCheck row) => new { row.Enabled, row.IntervalMinutes, Warn = row.WarnThreshold, Critical = row.CriticalThreshold };

    private static void CheckThreshold(string key, Optional<long> value, ThresholdRange? range, Action<string, MessageRef> error)
    {
        if (value is not { IsSet: true, Value: { } v }) return;
        if (range is null) error(key, MessageRef.Of(MessageText.HealthCheckNoThreshold));
        else if (v < range.Min || v > range.Max) error(key, MessageRef.Of(MessageText.HealthCheckOutOfRange, range.Min, range.Max));
    }
}

/// <summary>立即檢查（§17.2.4 第 4 步）：同步跑該檢查（最長 60 秒）並回結果；同一檢查 60 秒內只能按一次。</summary>
public sealed record RunHealthCheckRequest(string Code) : IRequest<ServiceResult>, ISystemAdminRequest;

/// <summary>立即檢查的節流（只在本實例記憶體；技術限制，§17.9）。</summary>
public sealed class HealthRunGate(TimeProvider time)
{
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);

    public bool TryEnter(string code)
    {
        var now = time.GetUtcNow();
        while (true)
        {
            if (_last.TryGetValue(code, out var last))
            {
                if (now - last < MinGap) return false;
                if (_last.TryUpdate(code, now, last)) return true;
            }
            else if (_last.TryAdd(code, now)) return true;
        }
    }
}

public sealed class RunHealthCheckHandler(
    HealthCheckEngine engine, HealthRunGate gate, OpsMonitorReader reader, IHealthCheckRepository checks, IOpsIncidentRepository incidents)
    : IRequestHandler<RunHealthCheckRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(RunHealthCheckRequest r, CancellationToken ct)
    {
        if (HealthCheckCatalog.Find(r.Code) is not { } def) return ServiceResult.NotFound(MessageRef.Of(MessageText.HealthCheckNotFound));
        if (!reader.Applicable(def)) return ServiceResult.Conflict(MessageRef.Of(MessageText.HealthCheckNotApplicable), "healthCheckNotApplicable");
        if (!gate.TryEnter(def.Code))
            return ServiceResult.Conflict(MessageRef.Of(MessageText.HealthCheckBusy, (int)HealthRunGate.MinGap.TotalSeconds), "healthCheckBusy");
        var row = (await checks.ListAsync(ct)).FirstOrDefault(c => c.Code == def.Code);
        // 背景（看門狗、ops.monitor）正在跑同一個檢查：同樣回 busy，不重疊執行
        if (await engine.TryRunNowAsync(def, EffectiveHealthSettings.Of(def, row), ct) is null)
            return ServiceResult.Conflict(MessageRef.Of(MessageText.HealthCheckBusy, (int)HealthRunGate.MinGap.TotalSeconds), "healthCheckBusy");
        row = (await checks.ListAsync(ct)).FirstOrDefault(c => c.Code == def.Code);
        return ServiceResult.Success(await reader.StateAsync(def, row, await incidents.ListUnresolvedAsync(ct), ct));
    }
}

/// <summary>確認（有人認領，停止重複提醒；O2-U4）。</summary>
public sealed record AcknowledgeIncidentRequest(long Id, string? ExpectedStatus)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("incident.acknowledge", AuditTargets.Incident, Id);
}

/// <summary>手動結案（健康檢查、通道、暫停類不行，§17.2.2）；note 進稽核摘要。</summary>
public sealed record ResolveIncidentRequest(long Id, string? ExpectedStatus, string? Note)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public const int NoteMax = 500;
    public AuditAction Audit => AuditAction.For("incident.resolve", AuditTargets.Incident, Id);
}

public sealed class IncidentStateHandlers(
    IOpsIncidentRepository incidents, OpsMonitorReader reader, ICurrentUser user, AuditContext audit, TimeProvider time)
    : IRequestHandler<AcknowledgeIncidentRequest, ServiceResult>, IRequestHandler<ResolveIncidentRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AcknowledgeIncidentRequest r, CancellationToken ct)
    {
        if (r.ExpectedStatus != "open") return ServiceResult.Invalid("expectedStatus", MessageRef.Of(MessageText.OpsExpectedStatusInvalid, "open"));
        if (await incidents.GetAsync(r.Id, ct) is not { } i) return ServiceResult.NotFound(MessageRef.Of(MessageText.IncidentNotFound));
        if (!await incidents.TryAcknowledgeAsync(i.Id, user.UserId, time.GetUtcNow().UtcDateTime, ct)) return StateChanged();
        audit.Target(label: i.TitleKey);
        return ServiceResult.Success(reader.Summary((await incidents.GetAsync(i.Id, ct))!));
    }

    public async Task<ServiceResult> Handle(ResolveIncidentRequest r, CancellationToken ct)
    {
        var expected = r.ExpectedStatus switch { "open" => IncidentStatus.Open, "acknowledged" => IncidentStatus.Acknowledged, _ => (IncidentStatus?)null };
        if (expected is null) return ServiceResult.Invalid("expectedStatus", MessageRef.Of(MessageText.OpsExpectedStatusInvalid, "open, acknowledged"));
        if (r.Note is { Length: > ResolveIncidentRequest.NoteMax })
            return ServiceResult.Invalid("note", MessageRef.Of(MessageText.OpsNoteTooLong, ResolveIncidentRequest.NoteMax));
        if (await incidents.GetAsync(r.Id, ct) is not { } i) return ServiceResult.NotFound(MessageRef.Of(MessageText.IncidentNotFound));
        if (!OpsMonitorReader.ManualResolvable(i))
            return ServiceResult.Conflict(MessageRef.Of(MessageText.IncidentManagedBySource), "incidentManagedBySource");
        if (!await incidents.TryResolveAsync(i.Id, expected.Value, IncidentResolvedReason.Manual, user.UserId, time.GetUtcNow().UtcDateTime, ct))
            return StateChanged();
        audit.Target(label: i.TitleKey);
        if (r.Note is { Length: > 0 } note) audit.Detail("note", note);
        return ServiceResult.Success(reader.Summary((await incidents.GetAsync(i.Id, ct))!));
    }

    private static ServiceResult StateChanged() =>
        ServiceResult.Conflict(MessageRef.Of(MessageText.IncidentStateChanged), "incidentStateChanged");
}
