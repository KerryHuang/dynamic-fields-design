using System.Data.Common;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Ops;
using DynamicModules.TestSupport;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>o2b 測試共用：直接送事件訊號、讀事件／樣本／通知／健康檢查列、建第二個實例的執行器與收件匣。</summary>
internal static class OpsIncidentKit
{
    public const string Base = "/api/v3/dynamic/system/ops";

    /// <summary>把測試主機的 TimeProvider 撥到指定時間（可往回撥）。</summary>
    public static void SetTime(TestApp app, DateTimeOffset at) => app.Time.Advance(at - app.Time.GetUtcNow());

    public static IncidentSignal Defect(string fingerprint, DateTime occurredUtc, IncidentSeverity severity = IncidentSeverity.Warning,
        IncidentSampleData? sample = null) => new()
    {
        Fingerprint = fingerprint, Source = IncidentSource.Server, Category = IncidentCategory.Defect, Severity = severity,
        Code = "InvalidOperationException", TitleKey = IncidentTitles.ServerError,
        TitleParams = IncidentTitles.Server("System.InvalidOperationException", "/api/x", null), OccurredUtc = occurredUtc, Version = "test",
        Sample = sample,
    };

    public static async Task<IncidentRecordResult> Record(TestApp app, IncidentSignal signal)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new RecordIncidentSignalRequest(signal));
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message ?? result.Code);
        return (IncidentRecordResult)result.Data!;
    }

    public static async Task<ServiceResult> Send(TestApp app, IRequest<ServiceResult> request)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    public static async Task<List<OpsIncident>> Incidents(TestApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<OpsIncident>().AsNoTracking().OrderBy(i => i.Id).ToListAsync();
    }

    public static async Task<OpsIncident> Incident(TestApp app, string fingerprint) =>
        (await Incidents(app)).Single(i => i.Fingerprint == fingerprint);

    public static async Task<List<OpsIncidentSample>> Samples(TestApp app, long incidentId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<OpsIncidentSample>().AsNoTracking()
            .Where(s => s.IncidentId == incidentId).OrderBy(s => s.Id).ToListAsync();
    }

    /// <summary>維運事件的站內通知（每位收件人一列）。</summary>
    public static async Task<List<Notification>> Notifications(TestApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<Notification>().AsNoTracking()
            .Where(n => n.Kind == NotificationKinds.OpsIncident).OrderBy(n => n.Id).ToListAsync();
    }

    public static async Task<List<HealthCheckResult>> Results(TestApp app, string code)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<HealthCheckResult>().AsNoTracking()
            .Where(r => r.Code == code).OrderBy(r => r.CheckedUtc).ThenBy(r => r.Id).ToListAsync();
    }

    public static async Task<HealthCheck> Check(TestApp app, string code)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<HealthCheck>().AsNoTracking().SingleAsync(c => c.Code == code);
    }

    public static async Task EnsureChecks(TestApp app) => await Send(app, new EnsureHealthChecksRequest(app.Time.GetUtcNow().UtcDateTime));

    public static string HealthFingerprint(string code) => ErrorFingerprint.ForInfra("infra", code);

    /// <summary>第二個（或指定的）實例的執行器：同一個資料庫、自己的狀態板與待補寫佇列。</summary>
    public static HealthCheckEngine Engine(TestApp app, params IHealthProbe[] probes) =>
        new(app.Services.GetRequiredService<IServiceScopeFactory>(), probes, app.Time, OpsInstance.Create(app.Time),
            app.Services.GetRequiredService<DirectAlertQueue>(), app.Services.GetRequiredService<ILogger<HealthCheckEngine>>());

    public static (IncidentInbox Inbox, IncidentInboxDrain Drain) Inbox(TestApp app)
    {
        var inbox = new IncidentInbox();
        return (inbox, new IncidentInboxDrain(inbox, app.Services.GetRequiredService<IServiceScopeFactory>(),
            app.Services.GetRequiredService<ILogger<IncidentInboxDrain>>()));
    }

    /// <summary>直接造一筆檢查結果（跳過探測），經彙總交易寫入。</summary>
    public static HealthObservation Observation(string code, DateTime at, HealthStatus status, long? value, string instance) =>
        new(code, Guid.NewGuid().ToString("N"), at, status, value, null, 1, instance);

    /// <summary>把磁碟探測換成可控的假磁碟（只量一個目錄）。</summary>
    public static void FakeDisk(IServiceCollection s, FakeDiskSpace disk)
    {
        s.RemoveAll<IDiskSpace>();
        s.AddSingleton<IDiskSpace>(disk);
        s.RemoveAll<OpsStoragePaths>();
        s.AddSingleton(new OpsStoragePaths(["/data"]));
    }
}

/// <summary>剩餘比例可設定的假磁碟（百分比）。</summary>
internal sealed class FakeDiskSpace : IDiskSpace
{
    public volatile int FreePercent = 50;

    public DiskUsage? Measure(string directory) => new(FreePercent * 1_000L, 100_000L);
}

/// <summary>依序回預先排好的結果的假探測（排完後重複最後一個）。</summary>
internal sealed class ScriptedProbe(string code, params ProbeResult[] script) : IHealthProbe
{
    private int _next;

    public string Code => code;

    public Task<ProbeResult> RunAsync(HealthProbeContext context, CancellationToken ct)
    {
        var i = Math.Min(_next, script.Length - 1);
        _next++;
        return Task.FromResult(script[i]);
    }
}

/// <summary>
/// 模擬資料庫唯讀（§17.12：取代設計原本「DI 換成 Mode=ReadOnly 的連線」）：開啟中的每條連線一打開就切換，
/// SQLite 用 <c>PRAGMA query_only</c>（寫入得到真的錯誤碼 8），PostgreSQL 用 session 預設唯讀交易（寫入得到真的 25006）。
/// 關掉時同樣在開啟時切回，連線池裡的連線不會殘留狀態。
/// </summary>
internal sealed class ReadOnlySwitch : DbConnectionInterceptor
{
    public volatile bool On;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Sql();
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Sql();
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private string Sql() => TestDatabase.IsPostgres
        ? $"SET SESSION CHARACTERISTICS AS TRANSACTION {(On ? "READ ONLY" : "READ WRITE")}"
        : $"PRAGMA query_only = {(On ? 1 : 0)}";
}

/// <summary>
/// 讓交易在<b>提交成功之後</b>丟例外（C14）：資料已寫入，呼叫端卻收到失敗，模擬「提交結果不明」。
/// 只在 <see cref="Armed"/> 大於 0 時作用，每丟一次減一。
/// </summary>
internal sealed class ThrowAfterCommit : DbTransactionInterceptor
{
    private int _armed;

    public int Armed
    {
        get => Volatile.Read(ref _armed);
        set => Volatile.Write(ref _armed, value);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Maybe();

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
    {
        Maybe();
        return Task.CompletedTask;
    }

    private void Maybe()
    {
        if (Interlocked.Decrement(ref _armed) >= 0) throw new InvalidOperationException("提交後丟例外（測試注入）");
        Interlocked.Exchange(ref _armed, 0);
    }
}
