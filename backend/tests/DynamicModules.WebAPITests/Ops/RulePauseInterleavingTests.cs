using System.Data.Common;
using System.Net;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Application.Dynamic.OpsService.RulePause;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.TestSupport;
using DynamicModules.WebAPITests.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>
/// 擋在補記的某一句之前（預設是第一句健康列寫入，即存在檢查之後）：讓補記停在那一刻，另一個交易在這時候進來。
/// 停住之後若看到別人送出含 <paramref name="other"/> 的指令（預設是鎖規則列的 <c>UPDATE "Dynamic_TaskRule"</c>），表示對方已到等鎖那一步。
/// </summary>
internal sealed class HoldWriteBackBeforeHealthWrite(string holdOn = "UPDATE \"Dynamic_RuleHealth\"", string other = "UPDATE \"Dynamic_TaskRule\"")
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
        if (_armed && sql.Contains(holdOn, StringComparison.Ordinal))
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
/// o2d 審查修正的確定性交錯（維運設計 §17.14 審查修正）：成功 reset 與失敗補記的先後、刪除與補記交錯。
/// 成功端直接用 <see cref="TaskRuleHealthGate"/>（與 <c>TaskRuleStage</c> 同一組呼叫），失敗端走真的存檔與補記，交錯點由測試排定。
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
    /// 再三次失敗共 4 不暫停，第五次暫停。舊失敗用三次：四次時 F 的補記自己就數到門檻而暫停（S 還沒提交、看不到；已知限制，§17.14 審查修正）。
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
    /// 必修 1b 的並行版：F 的補記已新增失敗列、還沒提交（停在清理失敗列之前，持有健康列的列鎖）時，S 的 reset 送出、等這把鎖；F 提交後 S 才執行。
    /// reset 的計數要看得到 F 剛提交的失敗列（PostgreSQL 等到列鎖後只重讀目標列，同一句的子查詢仍是舊快照，所以 reset 分兩句）。
    /// 只在 PostgreSQL（無鎖模式）：SQLite 的 S 在業務交易內、排在寫入閘門後面，不會跟 F 的補記交錯。
    /// S 送出後等 0.3 秒再放行 F（不夠時 S 排在 F 之後，測試照樣通過、不會誤紅）；reset 改回同一句帶子查詢時這個案例會紅（計數 0）。
    /// </summary>
    [PostgresFact]
    public async Task R1b_ResetWaitingOnWriteBackRowLock_CountsItsFailureRow()
    {
        var hold = new HoldWriteBackBeforeHealthWrite(holdOn: "DELETE FROM \"Dynamic_RuleFailure\"", other: "UPDATE \"Dynamic_RuleHealth\"");
        using var rig = new RulePauseRig(fileDatabase: true, withoutWriteSerialization: true, interceptor: hold);
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        rig.Faults.Fail(Rule);
        await rig.FailCreatesAsync(3);

        await using var s = rig.App.Services.CreateAsyncScope();
        var gate = s.ServiceProvider.GetRequiredService<TaskRuleHealthGate>();
        var rows = await gate.LoadAsync([id], CancellationToken.None);
        rig.Tick();
        var ts = gate.Now();

        rig.Tick();
        hold.Arm();
        var f = rig.PostRecordAsync();
        await hold.Reached;
        var reset = gate.OnSuccessAsync(rows, id, ts, CancellationToken.None);
        await Task.WhenAny(reset, hold.OtherRuleLockSent.WaitAsync(TimeSpan.FromMinutes(1)));
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        hold.Release();
        (await f).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await reset;

        var h = (await rig.HealthAsync(id))!;
        (h.ConsecutiveFailures, h.StreakResetUtc, h.PausedUtc).Should().Be((1, (DateTime?)ts, (DateTime?)null), "F 晚於 S，reset 後只剩 F");
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
