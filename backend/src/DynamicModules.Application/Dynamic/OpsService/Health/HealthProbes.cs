using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AppService.SharedMasters;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.Application.Dynamic.OpsService.Health;

public sealed record ProbeResult(HealthStatus Status, long? Value = null, string? MessageCode = null);

/// <summary>一次檢查的環境：專用 scope、生效設定、時間、本實例上一次的結果與計數器。</summary>
public sealed class HealthProbeContext(
    IServiceProvider services, HealthCheckDefinition definition, EffectiveHealthSettings settings, DateTime nowUtc, HealthObservation? previous,
    IDictionary<string, long> counters)
{
    public IServiceProvider Services { get; } = services;
    public HealthCheckDefinition Definition { get; } = definition;
    public EffectiveHealthSettings Settings { get; } = settings;
    public DateTime NowUtc { get; } = nowUtc;
    public HealthObservation? Previous { get; } = previous;
    public IDictionary<string, long> Counters { get; } = counters;
}

/// <summary>一個健康檢查的實作（目錄的每一列各一個，singleton）。</summary>
public interface IHealthProbe
{
    string Code { get; }
    Task<ProbeResult> RunAsync(HealthProbeContext context, CancellationToken ct);
}

/// <summary>磁碟剩餘空間；WebAPI 用 <c>DriveInfo</c> 實作（o2b 實測：macOS 上對目錄路徑回報該目錄所在磁碟）。</summary>
public interface IDiskSpace
{
    /// <summary>目錄不存在或量不到時回 null。</summary>
    DiskUsage? Measure(string directory);
}

public sealed record DiskUsage(long FreeBytes, long TotalBytes);

/// <summary><c>storage.diskFree</c> 要看的本機目錄（SQLite 資料庫檔目錄、附件根目錄、金鑰目錄；WebAPI 依部署設定給）。</summary>
public sealed record OpsStoragePaths(IReadOnlyList<string> Directories)
{
    public static readonly OpsStoragePaths None = new([]);
}

/// <summary>
/// provider 專屬的資料庫檢查（§17.7）：SQLite 的 WAL 檔大小與 PRAGMA 完整性檢查放在 Persistence，PostgreSQL 回「不適用」。
/// </summary>
public interface IDatabaseHealthProbe
{
    /// <summary>資料庫是檔案（SQLite）：WAL 檔大小與檔案層的完整性檢查才適用。</summary>
    bool SupportsFileChecks { get; }
    /// <summary><c>-wal</c> 檔的位元組數（讀檔案系統，不下 PRAGMA）；不適用回 null。</summary>
    Task<long?> WalBytesAsync(CancellationToken ct);
    /// <summary>檔案層的問題筆數（SQLite <c>quick_check</c>＋<c>foreign_key_check</c>）；不適用回 0。</summary>
    Task<long> FileIntegrityIssuesAsync(CancellationToken ct);
}

/// <summary>
/// <c>database.writable</c>：在交易型請求裡對自己那一列做條件式更新。唯讀、空間滿、連不上、其他失敗都是嚴重（訊息代碼四種）；
/// 忙碌不算失敗、沿用上一次狀態，連續忙碌達警告門檻才記警告 <c>busy</c>。
/// </summary>
public sealed class DatabaseWritableProbe(IDatabaseErrors errors) : IHealthProbe
{
    private const string BusyStreak = "database.writable.busy";

    public string Code => HealthCheckCatalog.DatabaseWritable;

    public async Task<ProbeResult> RunAsync(HealthProbeContext c, CancellationToken ct)
    {
        try
        {
            var result = await c.Services.GetRequiredService<ISender>().Send(new ProbeDatabaseWritableRequest(c.NowUtc), ct);
            if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
            c.Counters[BusyStreak] = 0;
            return new ProbeResult(HealthStatus.Ok);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            if (errors.IsReadOnly(e)) return Failed(c, "readOnly");
            if (errors.IsStorageFull(e)) return Failed(c, "full");
            if (errors.IsUnavailable(e)) return Failed(c, "unavailable");
            if (errors.IsBusyOrConflict(e))
            {
                var streak = c.Counters.TryGetValue(BusyStreak, out var n) ? n + 1 : 1;
                c.Counters[BusyStreak] = streak;
                if (c.Settings.Warn is { } warn && streak >= warn) return new ProbeResult(HealthStatus.Warning, streak, "busy");
                return c.Previous is { } p ? new ProbeResult(p.Status, p.Value, p.MessageCode) : new ProbeResult(HealthStatus.Pending);
            }
            return Failed(c, "failed");
        }
    }

    private static ProbeResult Failed(HealthProbeContext c, string code)
    {
        c.Counters[BusyStreak] = 0;
        return new ProbeResult(HealthStatus.Critical, null, code);
    }
}

/// <summary>寫入探測本身（交易型請求；失敗時交易直接回滾，§17.7）。</summary>
public sealed record ProbeDatabaseWritableRequest(DateTime NowUtc) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class ProbeDatabaseWritableHandler(IHealthCheckRepository checks) : IRequestHandler<ProbeDatabaseWritableRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ProbeDatabaseWritableRequest request, CancellationToken ct)
    {
        await checks.TouchProbeAsync(HealthCheckCatalog.DatabaseWritable, request.NowUtc, ct);
        return ServiceResult.Success();
    }
}

/// <summary><c>storage.diskFree</c>：各目錄所在磁碟的剩餘萬分比取最小的；訊息代碼帶不出位元組，數值是萬分比。</summary>
public sealed class DiskFreeProbe : IHealthProbe
{
    public string Code => HealthCheckCatalog.StorageDiskFree;

    public Task<ProbeResult> RunAsync(HealthProbeContext c, CancellationToken ct)
    {
        var disk = c.Services.GetService<IDiskSpace>();
        var paths = c.Services.GetService<OpsStoragePaths>() ?? OpsStoragePaths.None;
        if (disk is null || paths.Directories.Count == 0) return Task.FromResult(new ProbeResult(HealthStatus.NotConfigured));
        long? worst = null;
        foreach (var dir in paths.Directories)
        {
            if (disk.Measure(dir) is not { TotalBytes: > 0 } u) continue;
            var bp = u.FreeBytes * 10000 / u.TotalBytes;
            if (worst is null || bp < worst) worst = bp;
        }
        if (worst is not { } value) return Task.FromResult(new ProbeResult(HealthStatus.NotConfigured));
        return Task.FromResult(new ProbeResult(HealthCheckCatalog.Evaluate(c.Definition, c.Settings, value), value));
    }
}

/// <summary>
/// <c>scheduler.jobs</c>：最大連續失敗次數；另外任一工作的下次預定時間落後超過 <c>ops.schedulerLagIntervals</c> 個間隔直接判嚴重（<c>lagging</c>）。
/// 工作清單執行時才從 scope 取（ops.monitor 本身也是工作、又依賴執行器，建構時注入會形成循環）。
/// </summary>
public sealed class SchedulerJobsProbe : IHealthProbe
{
    public string Code => HealthCheckCatalog.SchedulerJobs;

    public async Task<ProbeResult> RunAsync(HealthProbeContext c, CancellationToken ct)
    {
        var rows = await c.Services.GetRequiredService<IScheduledJobRepository>().ListAsync(ct);
        var policies = c.Services.GetRequiredService<IPolicyReader>();
        var lag = await policies.IntAsync(PolicyCatalog.OpsSchedulerLagIntervals, null, ct);
        var byKey = c.Services.GetServices<IScheduledJob>().ToDictionary(j => j.Key, StringComparer.Ordinal);
        long maxFailures = rows.Count == 0 ? 0 : rows.Max(r => (long)r.ConsecutiveFailures);
        foreach (var row in rows)
        {
            if (!byKey.TryGetValue(row.JobKey, out var job)) continue;
            var interval = await policies.IntAsync(job.Interval, null, ct);
            if (c.NowUtc - row.NextDueUtc > TimeSpan.FromMinutes((double)interval * lag))
                return new ProbeResult(HealthStatus.Critical, maxFailures, "lagging");
        }
        return new ProbeResult(HealthCheckCatalog.Evaluate(c.Definition, c.Settings, maxFailures), maxFailures);
    }
}

public sealed class WalSizeProbe : IHealthProbe
{
    public string Code => HealthCheckCatalog.DatabaseWalSize;

    public async Task<ProbeResult> RunAsync(HealthProbeContext c, CancellationToken ct)
    {
        var bytes = await c.Services.GetRequiredService<IDatabaseHealthProbe>().WalBytesAsync(ct);
        return bytes is { } b ? new ProbeResult(HealthCheckCatalog.Evaluate(c.Definition, c.Settings, b), b) : new ProbeResult(HealthStatus.NotApplicable);
    }
}

/// <summary><c>database.integrity</c>：SQLite 的檔案層檢查＋兩種資料庫都做的共用主檔檢查（<c>MasterLifecycle.CheckAsync</c>）。</summary>
public sealed class IntegrityProbe : IHealthProbe
{
    public string Code => HealthCheckCatalog.DatabaseIntegrity;

    public async Task<ProbeResult> RunAsync(HealthProbeContext c, CancellationToken ct)
    {
        var issues = await c.Services.GetRequiredService<IDatabaseHealthProbe>().FileIntegrityIssuesAsync(ct)
            + (await c.Services.GetRequiredService<MasterLifecycle>().CheckAsync(ct)).Count;
        return new ProbeResult(HealthCheckCatalog.Evaluate(c.Definition, c.Settings, issues), issues);
    }
}

/// <summary><c>backup.freshness</c>：備份還沒做（路線階段 5），回「未設定」（B20）。</summary>
public sealed class BackupFreshnessProbe : IHealthProbe
{
    public string Code => HealthCheckCatalog.BackupFreshness;

    public Task<ProbeResult> RunAsync(HealthProbeContext c, CancellationToken ct) => Task.FromResult(new ProbeResult(HealthStatus.NotConfigured));
}

/// <summary><c>security.loginFailures</c>：稽核表最近 <c>ops.loginFailureWindowMinutes</c> 分鐘的 <c>login.failed</c> 列數。</summary>
public sealed class LoginFailuresProbe : IHealthProbe
{
    public string Code => HealthCheckCatalog.SecurityLoginFailures;

    public async Task<ProbeResult> RunAsync(HealthProbeContext c, CancellationToken ct)
    {
        var window = await c.Services.GetRequiredService<IPolicyReader>().IntAsync(PolicyCatalog.OpsLoginFailureWindowMinutes, null, ct);
        long count = await c.Services.GetRequiredService<IAuditLogRepository>().CountSinceAsync("login.failed", c.NowUtc.AddMinutes(-window), ct);
        return new ProbeResult(HealthCheckCatalog.Evaluate(c.Definition, c.Settings, count), count);
    }
}
