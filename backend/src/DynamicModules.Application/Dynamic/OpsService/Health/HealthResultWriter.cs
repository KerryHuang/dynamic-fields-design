using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Ops;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.OpsService.Health;

/// <summary>
/// 彙總交易（維運設計 §17.2.4 第 2 步）：鎖檢查列 → 以 <c>TransitionId</c> 去重 → 新增歷史結果 → 以這筆的檢查時間 R 彙總
/// → 只往後推的目前投影 → 同一交易開、更新或結案事件。結果列、事件轉換、站內通知、寄送佇列同一交易，有結果列就代表其他也都在。
/// <paramref name="Superseded"/>：補寫時同一檢查在待補寫佇列後面已經回到正常，這筆的開啟、升級信已經過時（§17.2.6）。
/// </summary>
public sealed record RecordHealthResultRequest(HealthObservation Observation, DateTime? NextDueUtc, bool Superseded = false)
    : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class RecordHealthResultHandler(
    IHealthCheckRepository checks, IncidentRecorder recorder, IOpsEnvironment env, IUnitOfWork uow, IDatabaseErrors errors, TimeProvider time)
    : IRequestHandler<RecordHealthResultRequest, ServiceResult>
{
    public const string MissingCode = "healthCheckRowMissing";

    public async Task<ServiceResult> Handle(RecordHealthResultRequest request, CancellationToken ct)
    {
        var o = request.Observation;
        var def = HealthCheckCatalog.Find(o.Code) ?? throw new InvalidOperationException($"未登記的健康檢查 {o.Code}");
        // 第一句：取得檢查列的列鎖，同一檢查的多個實例在 PostgreSQL 上依序彙總
        if (!await checks.LockAsync(o.Code, time.GetUtcNow().UtcDateTime, ct)) return ServiceResult.Conflict(null, MissingCode);
        // 持久化的去重：有這筆結果列就表示這次結果已完整處理過
        if (await checks.ResultExistsAsync(o.TransitionId, ct)) return ServiceResult.Success(false);
        checks.AddResult(new HealthCheckResult
        {
            Code = o.Code, CheckedUtc = o.CheckedUtc, Status = o.Status, Value = o.Value, MessageCode = o.MessageCode,
            DurationMs = o.DurationMs, InstanceId = o.InstanceId, TransitionId = o.TransitionId,
        });
        try
        {
            await uow.CommitAsync(ct);
        }
        catch (Exception e) when (errors.IsUniqueViolation(e))
        {
            // 另一台或上一輪剛寫入同一筆：當成已處理
            uow.Discard();
            return ServiceResult.Success(false);
        }

        var row = (await checks.ListAsync(ct)).First(c => c.Code == o.Code);
        var settings = EffectiveHealthSettings.Of(def, row);
        var (overall, value, message, abnormal) = await AggregateAsync(def, settings, o, ct);

        if (overall is not (HealthStatus.Pending))
            await checks.UpdateProjectionAsync(o.Code, o.CheckedUtc, overall, value, message, ct);
        if (request.NextDueUtc is { } next) await checks.AdvanceNextDueAsync(o.Code, next, ct);

        if (Signal(def, settings, o, overall, value, abnormal) is { } signal)
            await recorder.RecordAsync(signal with { Backfill = new AlertBackfill(o.TransitionId, request.Superseded) }, ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(true);
    }

    /// <summary>R 時的整體狀態：instance 範圍取在線實例（R 往前三個檢查間隔內有結果）裡最糟的；global 取這一筆。</summary>
    private async Task<(HealthStatus Status, long? Value, string? Message, int Abnormal)> AggregateAsync(
        HealthCheckDefinition def, EffectiveHealthSettings settings, HealthObservation o, CancellationToken ct)
    {
        if (def.Scope == HealthScope.Global) return (o.Status, o.Value, o.MessageCode, HealthCheckCatalog.Rank(o.Status) > 0 ? 1 : 0);
        var since = o.CheckedUtc - TimeSpan.FromMinutes((double)settings.IntervalMinutes * HealthCheckCatalog.OnlineIntervals);
        var latest = await checks.LatestPerInstanceAsync(o.Code, o.CheckedUtc, since, ct);
        if (latest.Count == 0) return (o.Status, o.Value, o.MessageCode, 0);
        var worst = latest.OrderByDescending(r => HealthCheckCatalog.Rank(r.Status)).ThenBy(r => r.InstanceId, StringComparer.Ordinal).First();
        // 全部是不開事件的狀態（正常、未設定…）：有任一正常就算正常
        var status = HealthCheckCatalog.Rank(worst.Status) > 0 ? worst.Status
            : latest.Any(r => r.Status == HealthStatus.Ok) ? HealthStatus.Ok : worst.Status;
        return (status, worst.Value, worst.MessageCode, latest.Count(r => HealthCheckCatalog.Rank(r.Status) > 0));
    }

    private IncidentSignal? Signal(HealthCheckDefinition def, EffectiveHealthSettings s, HealthObservation o, HealthStatus overall, long? value, int abnormal)
    {
        var rank = HealthCheckCatalog.Rank(overall);
        if (rank == 0 && overall != HealthStatus.Ok) return null;   // 未設定、不適用、還沒有結果：不開事件
        return new IncidentSignal
        {
            Fingerprint = ErrorFingerprint.ForInfra("infra", def.Code), Source = IncidentSource.Health, Category = def.Category,
            Severity = overall == HealthStatus.Critical ? IncidentSeverity.Critical : IncidentSeverity.Warning,
            Code = def.Code, TitleKey = IncidentTitles.HealthPrefix + def.Code,
            TitleParams = IncidentTitles.Health(HealthCheckCatalog.Camel(overall), value, s.Warn, s.Critical, abnormal),
            OccurredUtc = o.CheckedUtc, Version = env.Version, Recovered = overall == HealthStatus.Ok,
        };
    }
}

/// <summary>補齊缺的檢查列（比照 B7「插入，撞唯一鍵就略過」）：新列立刻到期。撞鍵回 Conflict（交易回滾，呼叫端當成別人已補）。</summary>
public sealed record EnsureHealthChecksRequest(DateTime NowUtc) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class EnsureHealthChecksHandler(IHealthCheckRepository checks, IUnitOfWork uow, IDatabaseErrors errors)
    : IRequestHandler<EnsureHealthChecksRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(EnsureHealthChecksRequest request, CancellationToken ct)
    {
        var existing = (await checks.ListAsync(ct)).Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var def in HealthCheckCatalog.All.Where(d => !existing.Contains(d.Code)))
        {
            checks.Add(new HealthCheck { Code = def.Code, NextDueUtc = request.NowUtc });
            added++;
        }
        if (added == 0) return ServiceResult.Success(0);
        try
        {
            await uow.CommitAsync(ct);
        }
        catch (Exception e) when (errors.IsUniqueViolation(e))
        {
            uow.Discard();
            return ServiceResult.Conflict(null, "healthCheckExists");
        }
        return ServiceResult.Success(added);
    }
}
