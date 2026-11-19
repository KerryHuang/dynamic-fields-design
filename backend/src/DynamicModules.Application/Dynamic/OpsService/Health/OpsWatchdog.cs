using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.OpsService.Health;

/// <summary>
/// 看門狗（維運設計 §17.2.4、O2-T6）：每個實例各一個，不經排程器租約。每輪跑執行者是看門狗、且到期的檢查，最後補寫待補寫佇列。
/// 理由：資料庫唯讀或連不上時排程器連租約都搶不到（搶租約本身是 UPDATE），看門狗只依賴「讀」，結果先放記憶體。
/// 檢查設定讀資料庫，失敗時用上一次讀到的值，從沒讀到過就用出廠值。WebAPI 的 OpsWatchdogHostedService 每 60 秒呼叫一次。
/// 另有一條寄送迴圈（<see cref="SendDirectAsync"/>，同一個 hosted service 的第二個背景迴圈）：資料庫寫不進時的直送信（§17.2.6），
/// 寄送卡住不會拖住檢查。
/// </summary>
public sealed class OpsWatchdog(HealthCheckEngine engine, DirectAlertQueue direct, IServiceScopeFactory scopes, TimeProvider time, ILogger<OpsWatchdog> logger)
{
    /// <summary>輪詢頻率（技術限制，§17.9；每個檢查的間隔另外可調）。</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, DateTime> _lastRun = new(StringComparer.Ordinal);
    private Dictionary<string, HealthCheck>? _settings;
    private bool _ensured;

    public async Task TickAsync(CancellationToken ct)
    {
        var rows = await LoadRowsAsync(ct);
        foreach (var def in HealthCheckCatalog.All.Where(d => d.Runner == HealthRunner.Watchdog))
        {
            if (ct.IsCancellationRequested) return;
            var settings = EffectiveHealthSettings.Of(def, rows.GetValueOrDefault(def.Code));
            if (!settings.Enabled) continue;
            var now = time.GetUtcNow().UtcDateTime;
            if (_lastRun.TryGetValue(def.Code, out var last) && now < last + TimeSpan.FromMinutes(settings.IntervalMinutes)) continue;
            _lastRun[def.Code] = now;
            await engine.RunAsync(def, settings, null, ct);
        }
        // 前面沒有新結果時，仍把之前留下的待補寫送一次
        await engine.FlushAsync(ct);
    }

    /// <summary>寄送迴圈的一輪：送直送佇列裡到期的信（交易外、單封逾時 30 秒）。</summary>
    public Task SendDirectAsync(CancellationToken ct) => direct.SendDueAsync(ct);

    private async Task<IReadOnlyDictionary<string, HealthCheck>> LoadRowsAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (!_ensured)
            {
                var ensured = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new EnsureHealthChecksRequest(time.GetUtcNow().UtcDateTime), ct);
                _ensured = ensured.IsSuccess;
            }
            var rows = await scope.ServiceProvider.GetRequiredService<IHealthCheckRepository>().ListAsync(ct);
            _settings = rows.ToDictionary(r => r.Code, StringComparer.Ordinal);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "看門狗讀不到健康檢查設定，{Fallback}", _settings is null ? "用出廠值" : "沿用上一次讀到的值");
        }
        return _settings ?? new Dictionary<string, HealthCheck>();
    }
}

/// <summary>
/// <c>ops.monitor</c> 排程工作（§17.2.8）：先送告警寄送佇列（§17.2.6），再跑執行者是排程、且到期的檢查（多實例時只跑一份）。
/// 寄送失敗（租約之外的例外）只記 Warning、本輪記部分失敗，不擋後面的檢查。不適用於這個資料庫種類的檢查（PostgreSQL 的 WAL 檔大小）不跑。
/// </summary>
public sealed class OpsMonitorJob(HealthCheckEngine engine, AlertDispatcher dispatcher, ILogger<OpsMonitorJob> logger) : IScheduledJob
{
    public string Key => "ops.monitor";

    public PolicyDefinition Interval => PolicyCatalog.OpsMonitorScanMinutes;

    public async Task<JobOutcome> RunAsync(JobRunContext context, CancellationToken ct)
    {
        var sender = context.Services.GetRequiredService<ISender>();
        var failed = 0;
        try
        {
            await dispatcher.RunAsync(sender, context.Fence, ct);
        }
        catch (Exception e) when (e is not (JobLeaseLostException or OperationCanceledException))
        {
            logger.LogWarning(e, "告警寄送佇列這一輪送不完，下一輪再送");
            failed++;
        }
        var now = context.Fence().NowUtc;
        await sender.Send(new EnsureHealthChecksRequest(now), ct);
        var rows = (await context.Services.GetRequiredService<IHealthCheckRepository>().ListAsync(ct)).ToDictionary(r => r.Code, StringComparer.Ordinal);
        var fileDb = context.Services.GetRequiredService<IDatabaseHealthProbe>().SupportsFileChecks;
        foreach (var def in HealthCheckCatalog.All.Where(d => d.Runner == HealthRunner.Scheduler && (fileDb || !d.RequiresFileDatabase)))
        {
            ct.ThrowIfCancellationRequested();
            if (!rows.TryGetValue(def.Code, out var row)) continue;
            var settings = EffectiveHealthSettings.Of(def, row);
            if (!settings.Enabled || row.NextDueUtc > now) continue;
            var o = await engine.RunAsync(def, settings, now.AddMinutes(settings.IntervalMinutes), ct);
            if (o.Status == HealthStatus.Error) failed++;
        }
        return new JobOutcome(failed == 0 ? ScheduledJobStatus.Succeeded : ScheduledJobStatus.PartiallyFailed,
            failed == 0 ? null : $"{failed} 項失敗（告警寄送或健康檢查本身）");
    }
}
