using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DynamicModules.WebAPITests.Ops.OpsIncidentKit;
using static DynamicModules.WebAPITests.Ops.SchedulerTestKit;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>健康檢查、看門狗、待補寫與唯讀（維運設計 §17.2.4、§17.2.9、§17.8 C01–C10、C14、C15）。</summary>
[Trait("Category", "Postgres")]
public class HealthCheckTests
{
    private const string Disk = HealthCheckCatalog.StorageDiskFree, Writable = HealthCheckCatalog.DatabaseWritable;

    private static TestApp App(FakeDiskSpace disk, ReadOnlySwitch? readOnly = null, bool noLock = false, bool fileDatabase = false,
        FakeMailSender? mail = null)
    {
        var app = new TestApp(fileDatabase: fileDatabase)
        {
            WithoutWriteSerialization = noLock,
            ExtraServices = s =>
            {
                FakeDisk(s, disk);
                if (mail is not null) AlertKit.UseFakeMail(s, mail);
            },
        };
        if (readOnly is not null) app.Interceptors.Add(readOnly);
        return app;
    }

    private static OpsWatchdog Watchdog(TestApp app) => app.Services.GetRequiredService<OpsWatchdog>();

    private static HealthCheckEngine Engine(TestApp app) => app.Services.GetRequiredService<HealthCheckEngine>();

    /// <summary>快轉並跑一輪看門狗（預設 5 分鐘，三項檢查都到期）。</summary>
    private static async Task Tick(TestApp app, int minutes = 5)
    {
        app.Time.Advance(TimeSpan.FromMinutes(minutes));
        await Watchdog(app).TickAsync(CancellationToken.None);
    }

    private static async Task<int> AdminCount(TestApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AppUser>().CountAsync(u => u.IsActive && u.IsSystemAdmin);
    }

    private static Task<OpsIncident?> HealthIncident(TestApp app, string code) =>
        Incidents(app).ContinueWith(t => t.Result.SingleOrDefault(i => i.Fingerprint == HealthFingerprint(code)));

    [Fact]
    public async Task C01_C02_ReadOnly_BoardCritical_PendingKept_ThenBackfilledAndRecovered()
    {
        var readOnly = new ReadOnlySwitch();
        using var app = App(new FakeDiskSpace(), readOnly);
        await Tick(app);
        Engine(app).PendingCount.Should().Be(0);

        readOnly.On = true;
        await Tick(app);
        var board = Engine(app).Board[Writable];
        board.Status.Should().Be(HealthStatus.Critical);
        board.MessageCode.Should().Be("readOnly");
        Engine(app).PendingCount.Should().BeGreaterThan(0);
        (await HealthIncident(app, Writable)).Should().BeNull("資料庫寫不進去，事件留在待補寫");
        app.Services.GetRequiredService<DirectAlertQueue>().PendingTransitionIds().Should().ContainSingle("資料庫可寫的開啟改由直送");
        var overview = await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/overview");
        overview.GetProperty("memoryOnly").GetBoolean().Should().BeTrue();
        overview.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == Writable)
            .GetProperty("fromMemory").GetBoolean().Should().BeTrue();

        readOnly.On = false;
        await Tick(app);
        Engine(app).PendingCount.Should().Be(0);
        var incident = (await HealthIncident(app, Writable))!;
        incident.Status.Should().Be(IncidentStatus.Resolved);
        incident.ResolvedReason.Should().Be(IncidentResolvedReason.Recovered);
        incident.Severity.Should().Be(IncidentSeverity.Critical);
        (await Notifications(app)).Should().HaveCount(await AdminCount(app), "開啟時的通知隨補寫寫入");
        (await Results(app, Writable)).Select(r => r.Status).Should().Contain(HealthStatus.Critical).And.EndWith(HealthStatus.Ok);
        (await Check(app, Writable)).LastStatus.Should().Be(HealthStatus.Ok);
    }

    [Fact]
    public async Task C03_ReadOnly_SaveReturns503Unwritable_NoDefectIncident()
    {
        var readOnly = new ReadOnlySwitch();
        using var app = App(new FakeDiskSpace(), readOnly);
        var client = app.AsUser();   // 先啟動主機（啟動時要寫種子資料）
        readOnly.On = true;
        var res = await client.PutAsJsonAsync("/api/v3/dynamic/system/policies/ops.clientErrorCollection", new { value = false });
        readOnly.On = false;

        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        res.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(60));
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("databaseUnwritable");
        await app.Services.GetRequiredService<IncidentInboxDrain>().FlushAsync(CancellationToken.None);
        (await Incidents(app)).Should().BeEmpty();
    }

    [Fact]
    public async Task C04_DiskWarningThenCritical_ReopensAndNotifies_ThenRecovered()
    {
        var disk = new FakeDiskSpace { FreePercent = 15 };
        using var app = App(disk);
        await Tick(app);
        var incident = (await HealthIncident(app, Disk))!;
        incident.Severity.Should().Be(IncidentSeverity.Warning);
        var admins = await AdminCount(app);
        (await Notifications(app)).Should().HaveCount(admins);
        (await app.AsUser().PostAsJsonAsync($"{Base}/incidents/{incident.Id}/acknowledge", new { expectedStatus = "open" })).EnsureSuccessStatusCode();

        disk.FreePercent = 5;
        await Tick(app);
        incident = (await HealthIncident(app, Disk))!;
        incident.Severity.Should().Be(IncidentSeverity.Critical);
        incident.Status.Should().Be(IncidentStatus.Open, "升級時退回開啟");
        (await Notifications(app)).Should().HaveCount(admins * 2);

        disk.FreePercent = 30;
        await Tick(app);
        incident = (await HealthIncident(app, Disk))!;
        incident.Status.Should().Be(IncidentStatus.Resolved);
        incident.ResolvedReason.Should().Be(IncidentResolvedReason.Recovered);
        (await Check(app, Disk)).LastValue.Should().Be(3000);
    }

    [Fact]
    public async Task C05_SchedulerJobs_FailuresWarn_LagCritical()
    {
        using var app = App(new FakeDiskSpace());
        var runner = Runner(app, app.Time, app.Services.GetServices<IScheduledJob>());
        await runner.EnsureJobsAsync(CancellationToken.None);
        var now = app.Time.GetUtcNow().UtcDateTime;
        foreach (var key in new[] { "ops.upkeep", "ops.monitor" }) await Update(app, key, j => j.NextDueUtc = now.AddMinutes(1));
        await Update(app, "ops.upkeep", j => j.ConsecutiveFailures = 3);
        var engine = OpsIncidentKit.Engine(app, new SchedulerJobsProbe());
        var def = HealthCheckCatalog.Find(HealthCheckCatalog.SchedulerJobs)!;
        var settings = EffectiveHealthSettings.Of(def, null);

        var warn = await engine.RunAsync(def, settings, null, CancellationToken.None);
        warn.Status.Should().Be(HealthStatus.Warning);
        warn.Value.Should().Be(3);

        // ops.monitor 每 1 分鐘；落後超過 3 個間隔
        await Update(app, "ops.monitor", j => j.NextDueUtc = now.AddMinutes(-4));
        var lag = await engine.RunAsync(def, settings, null, CancellationToken.None);
        lag.Status.Should().Be(HealthStatus.Critical);
        lag.MessageCode.Should().Be("lagging");
    }

    [Fact]
    public async Task C06_UpdateThresholds_ValidatesOrderAndRange_NullRestoresFactory_Audited()
    {
        using var app = App(new FakeDiskSpace());
        var url = $"{Base}/health-checks/{Disk}";
        (await app.AsUser().PutAsJsonAsync(url, new { warn = 500, critical = 1000 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await app.AsUser().PutAsJsonAsync(url, new { intervalMinutes = 0 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await app.AsUser().PutAsJsonAsync($"{Base}/health-checks/{Writable}", new { enabled = false })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await app.AsUser("sales01").PutAsJsonAsync(url, new { warn = 2500 })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await app.AsUser().PutAsJsonAsync(url, new { warn = 2500, critical = 800 })).EnsureSuccessStatusCode();
        var row = await Check(app, Disk);
        (row.WarnThreshold, row.CriticalThreshold).Should().Be((2500L, 800L));
        var list = await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/health-checks");
        var item = list.EnumerateArray().Single(c => c.GetProperty("code").GetString() == Disk);
        item.GetProperty("warn").GetProperty("value").GetInt64().Should().Be(2500);
        item.GetProperty("warn").GetProperty("factory").GetInt64().Should().Be(2000);
        item.GetProperty("warn").GetProperty("isFactory").GetBoolean().Should().BeFalse();

        // 送 null 回出廠值；省略的欄位不變
        (await app.AsUser().PutAsJsonAsync(url, new Dictionary<string, object?> { ["warn"] = null })).EnsureSuccessStatusCode();
        row = await Check(app, Disk);
        (row.WarnThreshold, row.CriticalThreshold).Should().Be(((long?)null, 800L));

        await using var scope = app.Services.CreateAsyncScope();
        var audits = await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AuditLogEntry>().AsNoTracking()
            .Where(a => a.Action == "healthCheck.update").ToListAsync();
        audits.Should().HaveCount(2).And.OnlyContain(a => a.TargetKind == "healthCheck" && a.TargetId == Disk && a.SummaryJson != null);
    }

    [Fact]
    public async Task C07_SchedulerDisabled_WatchdogStillRunsItsThreeChecks()
    {
        using var app = App(new FakeDiskSpace());
        await Tick(app);
        foreach (var code in new[] { Writable, Disk, HealthCheckCatalog.SchedulerJobs })
            (await Results(app, code)).Should().ContainSingle(code).Which.Status.Should().Be(HealthStatus.Ok);
        (await Results(app, HealthCheckCatalog.DatabaseIntegrity)).Should().BeEmpty("排程型的檢查不由看門狗跑");
    }

    [Fact]
    public async Task C08_FileChecks_SqliteHasValues_PostgresNotApplicable()
    {
        using var app = App(new FakeDiskSpace(), fileDatabase: true);
        SetTime(app, Start);
        await EnsureChecks(app);
        var wal = await app.AsUser().PostAsync($"{Base}/health-checks/{HealthCheckCatalog.DatabaseWalSize}/run", null);
        var list = (await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/health-checks")).EnumerateArray()
            .Select(c => c.GetProperty("code").GetString()).ToList();
        if (TestDatabase.IsPostgres)
        {
            wal.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await wal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("healthCheckNotApplicable");
            list.Should().NotContain(HealthCheckCatalog.DatabaseWalSize);
        }
        else
        {
            wal.StatusCode.Should().Be(HttpStatusCode.OK, await wal.Content.ReadAsStringAsync());
            (await wal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("value").GetInt64().Should().BeGreaterThanOrEqualTo(0);
            list.Should().Contain(HealthCheckCatalog.DatabaseWalSize);
        }

        // ops.monitor：排程型檢查到期就跑（PostgreSQL 略過 WAL）
        var clock = new ManualClock(Start);
        var runner = Runner(app, clock, app.Services.GetServices<IScheduledJob>().Where(j => j.Key == "ops.monitor"));
        await runner.EnsureJobsAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(2));
        await runner.TickAsync(CancellationToken.None);
        (await Row(app, "ops.monitor")).LastStatus.Should().Be(ScheduledJobStatus.Succeeded);
        var integrity = (await Results(app, HealthCheckCatalog.DatabaseIntegrity)).Should().ContainSingle().Subject;
        integrity.Status.Should().Be(HealthStatus.Ok);
        integrity.Value.Should().Be(0);
        (await Check(app, HealthCheckCatalog.DatabaseIntegrity)).NextDueUtc.Should().BeAfter(Start.UtcDateTime.AddHours(23));
        (await Results(app, HealthCheckCatalog.BackupFreshness)).Should().ContainSingle().Which.Status.Should().Be(HealthStatus.NotConfigured);
        (await Results(app, HealthCheckCatalog.SecurityLoginFailures)).Should().ContainSingle().Which.Status.Should().Be(HealthStatus.Ok);
        if (TestDatabase.IsPostgres) (await Results(app, HealthCheckCatalog.DatabaseWalSize)).Should().BeEmpty();
    }

    [Fact]
    public async Task C09_DiskChangesWhileReadOnly_BackfilledInOrder_OpenEscalateResolve()
    {
        var readOnly = new ReadOnlySwitch();
        var disk = new FakeDiskSpace();
        var mail = new FakeMailSender();
        using var app = App(disk, readOnly, mail: mail);
        await AlertKit.Configure(app);
        await Tick(app);
        readOnly.On = true;
        foreach (var free in new[] { 15, 5, 30 })
        {
            disk.FreePercent = free;
            await Tick(app);
            // 第一輪（警告）之後直送一次：磁碟開啟、資料庫可寫開啟寄出；升級信還沒寄就回到正常（過時）
            if (free == 15) await Watchdog(app).SendDirectAsync(CancellationToken.None);
        }
        mail.Sent.Should().HaveCount(2);
        Engine(app).PendingSnapshot().Where(o => o.Code == Disk).Select(o => o.Status)
            .Should().Equal(HealthStatus.Warning, HealthStatus.Critical, HealthStatus.Ok);

        readOnly.On = false;
        await Tick(app, minutes: 1);
        Engine(app).PendingCount.Should().Be(0);
        var results = await Results(app, Disk);
        results.Select(r => r.Status).Should().Equal(HealthStatus.Ok, HealthStatus.Warning, HealthStatus.Critical, HealthStatus.Ok);
        results.Select(r => r.CheckedUtc).Should().BeInAscendingOrder();
        var incident = (await HealthIncident(app, Disk))!;
        incident.Status.Should().Be(IncidentStatus.Resolved);
        incident.Severity.Should().Be(IncidentSeverity.Critical);
        incident.OccurrenceCount.Should().Be(2);
        // 磁碟：開啟、升級各一組；資料庫可寫：開啟一組
        (await Notifications(app)).Should().HaveCount(3 * await AdminCount(app));
        var outbox = await AlertKit.Outbox(app);
        outbox.Where(r => r.IncidentId == incident.Id).Select(r => (r.Transition, r.Status)).Should().Equal(
            (AlertTransition.Opened, AlertOutboxStatus.SentDirect), (AlertTransition.Escalated, AlertOutboxStatus.Skipped));
        outbox.Where(r => r.IncidentId != incident.Id).Select(r => (r.Transition, r.Status)).Should().Equal((AlertTransition.Opened, AlertOutboxStatus.SentDirect));
        await AlertKit.Dispatch(app);
        mail.Sent.Should().HaveCount(2, "直送過的與過時的都不再寄");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C10_TwoInstances_WorstWins_SilentInstanceDropsOut(bool noLock)
    {
        using var app = App(new FakeDiskSpace(), noLock: noLock);
        await EnsureChecks(app);
        var t0 = app.Time.GetUtcNow().UtcDateTime.AddHours(-1);
        Task Write(string instance, int minutes, int percent) => Send(app, new RecordHealthResultRequest(
            Observation(Disk, t0.AddMinutes(minutes), percent >= 20 ? HealthStatus.Ok : percent >= 10 ? HealthStatus.Warning : HealthStatus.Critical,
                percent * 100L, instance), null));

        // A 異常、B 正常同時寫入：先後不同，事件都要是開啟的嚴重
        await Within(Task.WhenAll(Write("A", 0, 5), Write("B", 1, 30)));
        (await HealthIncident(app, Disk))!.Status.Should().Be(IncidentStatus.Open);

        await Write("B", 5, 30);
        var open = (await HealthIncident(app, Disk))!;
        open.Status.Should().Be(IncidentStatus.Open, "B 正常不結案");
        open.Severity.Should().Be(IncidentSeverity.Critical);
        (await Check(app, Disk)).LastStatus.Should().Be(HealthStatus.Critical);

        await Write("A", 6, 30);
        (await HealthIncident(app, Disk))!.Status.Should().Be(IncidentStatus.Resolved);

        await Write("A", 10, 5);
        (await HealthIncident(app, Disk))!.Status.Should().Be(IncidentStatus.Open);
        // A 停止回報超過三個間隔（15 分鐘）後退出彙總
        await Write("B", 26, 30);
        var resolved = (await HealthIncident(app, Disk))!;
        resolved.Status.Should().Be(IncidentStatus.Resolved);
        resolved.ReopenCount.Should().Be(1);
        (await Check(app, Disk)).LastStatus.Should().Be(HealthStatus.Ok);
    }

    [Fact]
    public async Task C14_CommitThenThrow_BackfillDedupes_NoDuplicateRowsOrNotifications()
    {
        var fault = new ThrowAfterCommit();
        using var app = new TestApp();
        app.Interceptors.Add(fault);
        await EnsureChecks(app);
        var def = HealthCheckCatalog.Find(Disk)!;
        var engine = OpsIncidentKit.Engine(app, new ScriptedProbe(Disk, new ProbeResult(HealthStatus.Critical, 500)));

        fault.Armed = 1;
        await engine.RunAsync(def, EffectiveHealthSettings.Of(def, null), null, CancellationToken.None);
        engine.PendingCount.Should().Be(1, "提交後丟例外：呼叫端不知道其實已寫入");
        (await Results(app, Disk)).Should().ContainSingle();
        var notified = (await Notifications(app)).Count;
        notified.Should().Be(await AdminCount(app));

        fault.Armed = 1;
        await engine.FlushAsync(CancellationToken.None);
        engine.PendingCount.Should().Be(1);

        await engine.FlushAsync(CancellationToken.None);
        engine.PendingCount.Should().Be(0);
        (await Results(app, Disk)).Should().ContainSingle("TransitionId 唯一，已提交的被去重");
        (await HealthIncident(app, Disk))!.OccurrenceCount.Should().Be(1);
        (await Notifications(app)).Should().HaveCount(notified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C15_LateBackfill_DoesNotOverrideNewerProjection_NorReopen(bool concurrent)
    {
        using var app = App(new FakeDiskSpace(), noLock: concurrent);
        await EnsureChecks(app);
        // 直接送觀測值、不經執行器：檢查時間由測試給，先截到資料庫的精度（PostgreSQL 只到微秒，CI run 44467123758）
        var t = Application.Common.StorageTime.ToMicroseconds(app.Time.GetUtcNow().UtcDateTime.AddHours(-1));
        Task Write(string instance, int minute, HealthStatus status) =>
            Send(app, new RecordHealthResultRequest(Observation(Disk, t.AddMinutes(minute), status, status == HealthStatus.Ok ? 3000 : 500, instance), null));

        await Write("B", 0, HealthStatus.Critical);
        await Write("B", 3, HealthStatus.Ok);
        (await HealthIncident(app, Disk))!.Status.Should().Be(IncidentStatus.Resolved);

        if (!concurrent)
        {
            await Write("A", 1, HealthStatus.Critical);
            (await Results(app, Disk)).Should().HaveCount(3);
            var check = await Check(app, Disk);
            check.LastStatus.Should().Be(HealthStatus.Ok);
            check.LastCheckedUtc.Should().Be(t.AddMinutes(3));
            (await HealthIncident(app, Disk))!.Status.Should().Be(IncidentStatus.Resolved, "已在 10:03 之後結案，舊結果只留歷史");
            await Write("A", 4, HealthStatus.Critical);
        }
        else await Within(Task.WhenAll(Write("A", 1, HealthStatus.Critical), Write("A", 4, HealthStatus.Critical)));

        var final = await Check(app, Disk);
        final.LastStatus.Should().Be(HealthStatus.Critical);
        final.LastCheckedUtc.Should().Be(t.AddMinutes(4));
        var incident = (await HealthIncident(app, Disk))!;
        incident.Status.Should().Be(IncidentStatus.Open);
        incident.Regression.Should().BeTrue();
        incident.LastSeenUtc.Should().Be(t.AddMinutes(4));
        (await Results(app, Disk)).Should().HaveCount(4);
    }

    /// <summary>
    /// 時間精度（審查修正，CI run 44467123758）：PostgreSQL 的 timestamp 只到微秒，.NET 的 DateTime 是 100 奈秒。
    /// 檢查時間帶著微秒以下的位數時，寫入成功後狀態板的同一筆結果不能被當成「比資料庫新」（否則總覽一直顯示只在記憶體）。
    /// </summary>
    [Fact]
    public async Task C16_SubMicrosecondCheckTime_PersistedResultNotTreatedAsNewerThanProjection()
    {
        using var app = App(new FakeDiskSpace());
        // 讓「現在」的 tick 落在微秒的中間（末位 5），PostgreSQL 存回來會少這 5 個 tick
        app.Time.Advance(TimeSpan.FromTicks(15 - app.Time.GetUtcNow().Ticks % 10));
        await Tick(app);
        Engine(app).PendingCount.Should().Be(0);

        var stored = (await Check(app, Disk)).LastCheckedUtc!.Value;
        Engine(app).Board[Disk].CheckedUtc.Should().Be(stored, "狀態板與資料庫的檢查時間是同一個值");
        var overview = await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/overview");
        overview.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == Disk)
            .GetProperty("fromMemory").GetBoolean().Should().BeFalse("這筆已寫進資料庫");
    }

    /// <summary>第一次呼叫卡住直到 <see cref="Release"/>（回嚴重），之後的呼叫立刻回正常。</summary>
    private sealed class HeldProbe(string code) : IHealthProbe
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public string Code => code;

        public async Task<ProbeResult> RunAsync(HealthProbeContext context, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) > 1) return new ProbeResult(HealthStatus.Ok, 3000);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new ProbeResult(HealthStatus.Critical, 500);
        }
    }

    /// <summary>
    /// 重疊的檢查（審查建議）：背景正在跑時立即檢查回 busy（共用執行中狀態）；較早開始、較晚完成的結果不蓋掉狀態板上較新的結果。
    /// </summary>
    [Fact]
    public async Task C17_OverlappingRuns_RunNowBusy_LaterStartedResultKeptOnBoard()
    {
        using var app = App(new FakeDiskSpace());
        await EnsureChecks(app);
        var probe = new HeldProbe(Disk);
        var engine = OpsIncidentKit.Engine(app, probe);
        var def = HealthCheckCatalog.Find(Disk)!;
        var settings = EffectiveHealthSettings.Of(def, await Check(app, Disk));

        var slow = engine.RunAsync(def, settings, null, CancellationToken.None);
        await Within(probe.Entered.Task);
        (await engine.TryRunNowAsync(def, settings, CancellationToken.None)).Should().BeNull("背景正在跑同一個檢查");

        app.Time.Advance(TimeSpan.FromMinutes(1));
        var later = await Within(engine.RunAsync(def, settings, null, CancellationToken.None));
        probe.Release.SetResult();
        var earlier = await Within(slow);

        earlier.CheckedUtc.Should().BeBefore(later.CheckedUtc);
        engine.Board[Disk].TransitionId.Should().Be(later.TransitionId, "較早開始的結果晚完成，不蓋掉較新的");
        (await engine.TryRunNowAsync(def, settings, CancellationToken.None)).Should().NotBeNull("都跑完了");
    }

    [Fact]
    public async Task Overview_RunNow_Throttled_ResultsTrend()
    {
        using var app = App(new FakeDiskSpace { FreePercent = 40 });
        await EnsureChecks(app);
        var run = await app.AsUser().PostAsync($"{Base}/health-checks/{Disk}/run", null);
        run.StatusCode.Should().Be(HttpStatusCode.OK, await run.Content.ReadAsStringAsync());
        (await run.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("ok");
        var again = await app.AsUser().PostAsync($"{Base}/health-checks/{Disk}/run", null);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("healthCheckBusy");
        app.Time.Advance(TimeSpan.FromSeconds(61));
        (await app.AsUser().PostAsync($"{Base}/health-checks/{Disk}/run", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await app.AsUser().PostAsync($"{Base}/health-checks/nope/run", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var trend = await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/health-checks/{Disk}/results?limit=1");
        trend.GetProperty("items").GetArrayLength().Should().Be(1);
        trend.GetProperty("truncated").GetBoolean().Should().BeTrue();

        var overview = await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/overview");
        overview.GetProperty("memoryOnly").GetBoolean().Should().BeFalse();
        overview.GetProperty("channel").GetProperty("configured").GetBoolean().Should().BeFalse();
        overview.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == Disk)
            .GetProperty("value").GetInt64().Should().Be(4000);
        (await app.AsUser("sales01").GetAsync($"{Base}/overview")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
