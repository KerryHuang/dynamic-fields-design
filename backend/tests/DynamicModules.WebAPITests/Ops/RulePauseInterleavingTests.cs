using System.Data.Common;
using System.Net;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Application.Dynamic.OpsService.RulePause;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.TestSupport;
using DynamicModules.WebAPITests.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>
/// 擋在補記的某一句之前（預設是第一句健康列寫入，即存在檢查之後）：讓補記停在那一刻，另一個交易在這時候進來。
/// 停住之後若看到別人送出含 <paramref name="other"/> 的指令（預設是鎖規則列的 <c>UPDATE "Dynamic_TaskRule"</c>），表示對方已到等鎖那一步。
/// 含 <paramref name="unless"/> 的指令不擋。
/// </summary>
internal sealed class HoldWriteBackBeforeHealthWrite(
    string holdOn = "UPDATE \"Dynamic_RuleHealth\"", string other = "UPDATE \"Dynamic_TaskRule\"", string? unless = null)
    : DbCommandInterceptor
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _otherLock = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _armed, _held;

    public void Arm() => _armed = true;

    public Task Reached => _reached.Task.WaitAsync(TimeSpan.FromMinutes(1));

    public Task OtherRuleLockSent => _otherLock.Task;

    public void Release() => _release.TrySetResult();

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        var sql = command.CommandText;
        if (_held && sql.Contains(other, StringComparison.Ordinal)) _otherLock.TrySetResult();
        if (_armed && sql.Contains(holdOn, StringComparison.Ordinal) && (unless is null || !sql.Contains(unless, StringComparison.Ordinal)))
        {
            _armed = false;
            _held = true;
            _reached.TrySetResult();
            await _release.Task.WaitAsync(TimeSpan.FromMinutes(1));
        }
        return result;
    }
}

/// <summary>
/// 用另一條連線（不進連線池）看 PostgreSQL 端真的形成的鎖等待（<c>pg_stat_activity</c>、<c>pg_blocking_pids</c>），以後端 pid 辨識連線。
/// 輪詢有上限，逾時丟例外讓測試失敗（不是略過）：攔截器只看得到「即將送出 SQL」，證明不了資料庫已在等鎖。
/// </summary>
internal static class PgLockWait
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>這個 scope 的 DbContext 目前那條連線的後端 pid（要在交易內取，連線才固定）。</summary>
    public static Task<int> PidAsync(IServiceProvider scope) =>
        scope.GetRequiredService<DynamicDbContext>().Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

    /// <summary>等到 pid 這條連線在等鎖。</summary>
    public static Task WaitingAsync(string connectionString, int pid) =>
        PollAsync(connectionString, "SELECT count(*) FROM pg_stat_activity WHERE pid = @p AND wait_event_type = 'Lock'", pid, null,
            $"pid {pid} 沒有在等鎖");

    /// <summary>等到有別的連線被 pid 這條連線擋住（等它的鎖）；<paramref name="orDone"/> 先完成也返回（交給呼叫端的斷言判斷）。</summary>
    public static Task BlockedByAsync(string connectionString, int pid, Task? orDone = null) =>
        PollAsync(connectionString,
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND @p = ANY(pg_blocking_pids(pid))",
            pid, orDone, $"沒有連線在等 pid {pid} 的鎖");

    private static async Task PollAsync(string connectionString, string sql, int pid, Task? orDone, string timeoutMessage)
    {
        await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await conn.OpenAsync();
        var deadline = DateTime.UtcNow + Limit;
        while (true)
        {
            if (orDone is { IsCompleted: true }) return;
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("p", pid);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException(timeoutMessage);
            await Task.Delay(20);
        }
    }
}

/// <summary>
/// o2d 審查修正的確定性交錯（維運設計 §17.14 審查修正）：成功 reset 與失敗補記的先後、刪除與補記交錯。
/// 成功端直接用 <see cref="TaskRuleHealthGate"/>（與 <c>TaskRuleStage</c> 同一組呼叫；並行案例包在真正的交易內），失敗端走真的存檔與補記，交錯點由測試排定，
/// PostgreSQL 上的鎖等待由 <see cref="PgLockWait"/> 在資料庫端確認。
/// </summary>
[Trait("Category", "Postgres")]
public class RulePauseInterleavingTests
{
    private const string Rule = "r-order";

    private static DateTime Now(TestApp app) => StorageTime.ToMicroseconds(app.Time.GetUtcNow().UtcDateTime);

    /// <summary>
    /// 必修 1a：Q 先讀健康列（還沒有列）；P 評估較早、失敗、補記提交、移出在途；Q 才以較晚的評估時間成功。
    /// Q 讀到的列是舊的、在途也已空，但 P 在 Q 讀之後才補記，Q 仍要寫 reset；之後四次失敗不含 P、不暫停。
    /// </summary>
    [Fact]
    public async Task R1a_FailureWrittenBackAfterSuccessLoadedHealth_SuccessStillResets()
    {
        using var rig = new RulePauseRig();
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);

        await using var q = rig.App.Services.CreateAsyncScope();
        var gate = q.ServiceProvider.GetRequiredService<TaskRuleHealthGate>();
        var rows = await gate.LoadAsync([id], CancellationToken.None);
        rows.Should().BeEmpty();

        rig.Faults.Fail(Rule);
        await rig.FailCreatesAsync(1);
        (await rig.HealthAsync(id))!.ConsecutiveFailures.Should().Be(1, "P 已補記");

        rig.Tick();
        var tq = gate.Now();
        await gate.OnSuccessAsync(rows, id, tq, CancellationToken.None);
        var h = (await rig.HealthAsync(id))!;
        (h.ConsecutiveFailures, h.StreakResetUtc).Should().Be((0, (DateTime?)tq), "Q 的成功晚於 P，P 不算進下一串");

        await rig.FailCreatesAsync(4);
        (await rig.HealthAsync(id))!.PausedUtc.Should().BeNull("Q 之後只有四次失敗");
        await rig.FailCreatesAsync(1);
        (await rig.HealthAsync(id))!.PausedUtc.Should().NotBeNull();
    }

    /// <summary>
    /// 必修 1b：舊失敗、成功 S（評估時間 tS）、較晚的失敗 F 先補記、S 才寫 reset：計數應為 1（只剩 F），<c>StreakResetUtc</c>＝tS；
    /// 再三次失敗共 4 不暫停，第五次暫停。成功端不包交易、依序呼叫，只驗 reset 的計數規則（舊失敗用三次，F 補記時不會先數到門檻）；
    /// 真正交易內「S 未提交、F 補記」的交錯見 <see cref="R1c_LaterFailureWhileEarlierSuccessUncommitted_DoesNotPause"/>。
    /// </summary>
    [Fact]
    public async Task R1b_LaterFailureWrittenBackBeforeEarlierSuccessReset_CountKeepsOnlyLaterFailures()
    {
        using var rig = new RulePauseRig();
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        rig.Faults.Fail(Rule);
        await rig.FailCreatesAsync(3);

        await using var s = rig.App.Services.CreateAsyncScope();
        var gate = s.ServiceProvider.GetRequiredService<TaskRuleHealthGate>();
        var rows = await gate.LoadAsync([id], CancellationToken.None);
        rig.Tick();
        var ts = gate.Now();

        await rig.FailCreatesAsync(1);
        (await rig.HealthAsync(id))!.ConsecutiveFailures.Should().Be(4, "F 先補記");

        await gate.OnSuccessAsync(rows, id, ts, CancellationToken.None);
        var h = (await rig.HealthAsync(id))!;
        (h.ConsecutiveFailures, h.StreakResetUtc, h.PausedUtc).Should().Be((1, (DateTime?)ts, (DateTime?)null), "S 之前的三次排除、S 之後的 F 保留");

        await rig.FailCreatesAsync(3);
        (await rig.HealthAsync(id))!.PausedUtc.Should().BeNull("S 之後只有四次失敗");
        await rig.FailCreatesAsync(1);
        h = (await rig.HealthAsync(id))!;
        (h.PausedUtc is not null, h.ConsecutiveFailures).Should().Be((true, 5));
    }

    /// <summary>
    /// 第二輪必修 1（誤暫停）：四次舊失敗；成功 S 在真正的業務交易內評估、還沒提交；較晚的失敗 F 這時存檔。
    /// F 不能在看不到 S 的情況下數到五而暫停（S 提交後的 reset 不會解除暫停）。修法：S 讀到連續失敗時，評估前先鎖規則列（與補記同一把），
    /// F 的業務交易或補記排在 S 提交之後，看得到 S 的 reset。PostgreSQL 無鎖模式下以 <c>pg_blocking_pids</c> 確認 F 真的在等 S 的鎖
    /// （修前 F 不等、直接補記完）；SQLite 的寫入交易一律排隊（BEGIN IMMEDIATE），F 本來就排在 S 之後，只驗結果。
    /// </summary>
    [Fact]
    public async Task R1c_LaterFailureWhileEarlierSuccessUncommitted_DoesNotPause()
    {
        using var rig = new RulePauseRig(fileDatabase: true, withoutWriteSerialization: true);
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        rig.Faults.Fail(Rule);
        await rig.FailCreatesAsync(4);

        await using var s = rig.App.Services.CreateAsyncScope();
        await using var tx = await s.ServiceProvider.GetRequiredService<ITransactionManager>().BeginAsync(CancellationToken.None);
        var gate = s.ServiceProvider.GetRequiredService<TaskRuleHealthGate>();
        var rows = await gate.LoadAsync([id], CancellationToken.None);
        rig.Tick();
        var ts = (await gate.BeginAsync(rows, id, CancellationToken.None))!.Value;

        rig.Tick();
        var f = rig.PostRecordAsync();
        if (TestDatabase.IsPostgres)
            await PgLockWait.BlockedByAsync(rig.App.ConnectionString, await PgLockWait.PidAsync(s.ServiceProvider), orDone: f);
        await gate.OnSuccessAsync(rows, id, ts, CancellationToken.None);
        await tx.CommitAsync(CancellationToken.None);
        await tx.DisposeAsync(); // 寫入閘門的名額在 Dispose 才放，F 要等它
        (await f).StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var h = (await rig.HealthAsync(id))!;
        (h.PausedUtc, h.ConsecutiveFailures, h.StreakResetUtc).Should().Be(((DateTime?)null, 1, (DateTime?)ts), "S 之前的四次排除，只剩 F");
        (await rig.IncidentsAsync(RuleErrorCodes.AutoPaused)).Should().BeEmpty("沒有暫停就沒有暫停事件");
        (await rig.PauseNotificationsAsync()).Should().BeEmpty();
        (await rig.AuditAsync("taskRule.autoPause")).Should().BeEmpty();
    }

    /// <summary>
    /// 必修 1b 的並行版（第二輪必修 2 重寫）：成功 S 在真正的交易內、評估前沒有先鎖（讀到的列沒有連續失敗），成功時才因較早的在途失敗要寫 reset；
    /// 較晚的失敗 F 的補記已新增失敗列、停在清理之前（持有健康列的列鎖）。S 的 reset 送出後，另一條連線確認 S 真的在等鎖才放行 F；
    /// reset 的計數要看得到 F 剛提交的失敗列（PostgreSQL 等到列鎖後只重讀目標列，同一句的子查詢仍是舊快照，所以 reset 分兩句）。
    /// 接著讓 S 停在兩句之間，送第三筆補記 G，確認 G 在等 S 的鎖（兩句之間不會被插隊、鎖持有到 S 提交）；最後計數＝F、G 兩筆。
    /// 只在 PostgreSQL（無鎖模式）：SQLite 的寫入交易一律排隊，S 與 F 不會交錯。reset 改回同一句帶子查詢時計數少一（F 沒數到）。
    /// </summary>
    [PostgresFact]
    public async Task R1b_ResetWaitingOnWriteBackRowLock_CountsItsFailureRow()
    {
        var holdF = new HoldWriteBackBeforeHealthWrite(holdOn: "DELETE FROM \"Dynamic_RuleFailure\"");
        var holdS = new HoldWriteBackBeforeHealthWrite(holdOn: "\"ConsecutiveFailures\" = (", unless: "\"StreakResetUtc\" =");
        using var rig = new RulePauseRig(fileDatabase: true, withoutWriteSerialization: true, interceptor: holdF);
        rig.App.Interceptors.Add(holdS);
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        rig.Faults.Fail(Rule);
        await rig.FailCreatesAsync(3);
        rig.Faults.Heal(Rule);
        rig.Tick();
        await rig.NewRecordAsync();
        (await rig.HealthAsync(id))!.ConsecutiveFailures.Should().Be(0, "先有一次成功，S 讀到的列沒有連續失敗、評估前不鎖");
        rig.Faults.Fail(Rule);

        await using var s = rig.App.Services.CreateAsyncScope();
        await using var tx = await s.ServiceProvider.GetRequiredService<ITransactionManager>().BeginAsync(CancellationToken.None);
        var spid = await PgLockWait.PidAsync(s.ServiceProvider);
        var gate = s.ServiceProvider.GetRequiredService<TaskRuleHealthGate>();
        var rows = await gate.LoadAsync([id], CancellationToken.None);
        rig.Tick();
        var ts = (await gate.BeginAsync(rows, id, CancellationToken.None))!.Value;

        rig.Tick();
        holdF.Arm();
        var f = rig.PostRecordAsync();
        await holdF.Reached;
        // 評估早於 S、在 S 評估之後才登記的在途失敗（失敗那次跑得較久）：S 成功時因此要寫 reset
        rig.App.Services.GetRequiredService<RuleFailureInFlight>().Register(id, ts.AddTicks(-10));
        holdS.Arm();
        var reset = gate.OnSuccessAsync(rows, id, ts, CancellationToken.None);
        await PgLockWait.WaitingAsync(rig.App.ConnectionString, spid);
        holdF.Release();
        (await f).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await Task.WhenAny(reset, holdS.Reached)).Should().NotBeNull();

        rig.Tick();
        var g = rig.SendAsync(new RecordRuleFailureRequest(RuleFailureTag.TaskRule, id, "InjectedFault", Now(rig.App), null));
        await PgLockWait.BlockedByAsync(rig.App.ConnectionString, spid);
        holdS.Release();
        await reset;
        await tx.CommitAsync(CancellationToken.None);
        await g;

        var h = (await rig.HealthAsync(id))!;
        (h.ConsecutiveFailures, h.StreakResetUtc, h.PausedUtc).Should().Be((2, (DateTime?)ts, (DateTime?)null), "S 之後只有 F、G");
        (await rig.FailuresAsync(id)).Select(x => x.FailedUtc > ts).Should().Equal(true, true);
    }

    /// <summary>
    /// 必修 2：補記已確認規則存在、停在第一句健康列寫入之前；這時刪規則（或刪模組）。刪除不能先提交而讓補記接著重建健康列與失敗列。
    /// 無鎖模式（只在 PostgreSQL 有作用）才真的交錯；有鎖時刪除排在補記之後，結果相同。刪除到鎖那一步、或一秒內沒動靜（被閘門擋住）就放行補記。
    /// </summary>
    [Theory]
    [InlineData("rule", false)]
    [InlineData("rule", true)]
    [InlineData("module", false)]
    [InlineData("module", true)]
    public async Task R2_DeleteWhileWriteBackHeld_LeavesNoOrphanRows(string path, bool noLock)
    {
        var hold = new HoldWriteBackBeforeHealthWrite();
        using var rig = new RulePauseRig(fileDatabase: true, withoutWriteSerialization: noLock, interceptor: hold);
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        rig.Faults.Fail(Rule);
        await rig.FailCreatesAsync(1);
        if (path == "module")
            (await rig.Admin.PostAsync($"{RulePauseRig.Base}/modules/{rig.Module}/deactivate", null)).EnsureSuccessStatusCode();

        rig.Tick();
        hold.Arm();
        var writeBack = rig.SendAsync(new RecordRuleFailureRequest(RuleFailureTag.TaskRule, id, "InjectedFault", Now(rig.App), null));
        await hold.Reached;
        var delete = path == "rule"
            ? rig.Planner.DeleteAsync($"{RulePauseRig.Base}/modules/{rig.Module}/task-rules/{Rule}")
            : rig.Admin.DeleteAsync($"{RulePauseRig.Base}/modules/{rig.Module}?purged=0");
        await Task.WhenAny(delete, hold.OtherRuleLockSent, Task.Delay(TimeSpan.FromSeconds(1)));
        hold.Release();
        await writeBack;
        var res = await delete;
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        (await rig.HealthAsync(id)).Should().BeNull("刪除與補記交錯後不留孤兒健康列");
        (await rig.FailuresAsync(id)).Should().BeEmpty("也不留孤兒失敗列");
    }

    /// <summary>
    /// 必修 2 的刪應用程式路徑：成員模組上的規則（隨模組清除）與掛在共用主檔上的規則（<c>ListByAppAsync</c>）。
    /// 先各補記一次失敗，再讓主檔上那條的補記停在健康列寫入之前、刪應用程式；兩條規則的健康列與失敗列都要清乾淨。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R2_DeleteAppWhileWriteBackHeld_LeavesNoOrphanRows(bool noLock)
    {
        var hold = new HoldWriteBackBeforeHealthWrite();
        var app = new TestApp(fileDatabase: true) { WithoutWriteSerialization = noLock };
        app.Interceptors.Add(hold);
        using var rig = new SharedMasterRig(app);
        await rig.ImportAsync(SharedMasterPackages.Bp);
        await rig.ImportAsync(AppWithRules);
        await rig.OpenOkAsync("rp");
        var ids = rig.Db(db => db.Set<TaskRule>().Where(r => r.Key == "rp-own" || r.Key == "rp-master").ToDictionary(r => r.Key, r => r.Id));
        ids.Should().HaveCount(2);
        foreach (var ruleId in ids.Values) await Send(app, new RecordRuleFailureRequest(RuleFailureTag.TaskRule, ruleId, "InjectedFault", Now(app), null));
        await rig.CloseOkAsync("rp");

        app.Time.Advance(TimeSpan.FromSeconds(1));
        hold.Arm();
        var writeBack = Send(app, new RecordRuleFailureRequest(RuleFailureTag.TaskRule, ids["rp-master"], "InjectedFault", Now(app), null));
        await hold.Reached;
        var delete = rig.DeleteAppOkAsync("rp");
        await Task.WhenAny(delete, hold.OtherRuleLockSent, Task.Delay(TimeSpan.FromSeconds(1)));
        hold.Release();
        await writeBack;
        await delete;

        var all = ids.Values.ToList();
        rig.Db(db => db.Set<RuleHealth>().Count(h => all.Contains(h.RuleId))).Should().Be(0);
        rig.Db(db => db.Set<RuleFailure>().Count(f => all.Contains(f.RuleId))).Should().Be(0);
    }

    private static async Task Send(TestApp app, RecordRuleFailureRequest request)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<MediatR.ISender>().Send(request);
        result.IsSuccess.Should().BeTrue(result.Message ?? result.Code);
    }

    /// <summary>應用程式 rp：自己的模組 rp-own（規則 rp-own）與掛在共用主檔 bp/partner 上的規則 rp-master。</summary>
    private const string AppWithRules = """
        {
          "format": "formwright-app", "formatVersion": 7,
          "manifest": { "id": "rp", "version": "1.0.0", "name": { "zh-TW": "規則暫停刪除測試" },
            "requires": { "masters": [ { "id": "bp", "modules": ["partner"] } ] } },
          "groups": [],
          "modules": [
            { "code": "own", "key": "rp-own", "name": { "zh-TW": "自有" }, "kind": "standalone", "titleField": "name",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "fields": [
                { "code": "name", "type": "text", "label": { "zh-TW": "名稱" }, "config": { "required": true } },
                { "code": "owner", "type": "user", "label": { "zh-TW": "負責人" }, "config": {} } ] } ],
              "permissions": [] }
          ],
          "contributions": [ {
            "master": "bp", "module": "partner",
            "block": { "code": "rp", "name": { "zh-TW": "任務" } },
            "fields": [ { "code": "accountMgr", "type": "user", "label": { "zh-TW": "客戶經理" }, "config": {} } ],
            "permissions": []
          } ],
          "taskRules": [
            { "code": "rp-own", "name": { "zh-TW": "自有" }, "active": true, "module": "own", "trigger": { "on": "manual" },
              "tasks": [ { "code": "a", "title": { "zh-TW": "跟進" }, "assignee": "own.owner" } ] },
            { "code": "rp-master", "name": { "zh-TW": "主檔" }, "active": true, "module": "bp/partner", "trigger": { "on": "manual" },
              "tasks": [ { "code": "a", "title": { "zh-TW": "拜訪" }, "assignee": "bp/partner.accountMgr" } ] }
          ]
        }
        """;
}
