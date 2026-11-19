using System.Net;
using System.Net.Http.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Application.Dynamic.OpsService.RulePause;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Ops;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Ops.SchedulerTestKit;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>o2d 規則失控自動暫停（維運設計 §17.2.7、§17.8 E01–E16、F01 的規則失敗列）。E13 例外矩陣另見 <see cref="RulePauseMatrixTests"/>。</summary>
[Trait("Category", "Postgres")]
public class RulePauseTests
{
    private const string Rule = "r-follow";

    private static async Task<(RulePauseRig Rig, long RuleId)> Paused(string on = "onCreate", object? when = null, bool fileDatabase = false)
    {
        var rig = new RulePauseRig(fileDatabase);
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule, on, when);
        rig.Faults.Fail(Rule);
        if (on == "onCreate") await rig.FailCreatesAsync(5);
        return (rig, id);
    }

    [Fact]
    public async Task E01_FiveFailures_PausesWithCriticalIncidentNotificationsAndAudit()
    {
        using var rig = new RulePauseRig();
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        rig.Faults.Fail(Rule);

        for (var n = 1; n <= 4; n++)
        {
            await rig.FailCreatesAsync(1);
            var h = (await rig.HealthAsync(id))!;
            h.ConsecutiveFailures.Should().Be(n);
            h.PausedUtc.Should().BeNull();
            h.LastErrorCode.Should().Be("InjectedFaultException");
        }
        (await rig.IncidentsAsync(RuleErrorCodes.AutoPaused)).Should().BeEmpty();
        await rig.FailCreatesAsync(1);

        var health = (await rig.HealthAsync(id))!;
        health.PausedUtc.Should().NotBeNull();
        health.PauseCount.Should().Be(1);
        var incident = (await rig.IncidentsAsync(RuleErrorCodes.AutoPaused)).Should().ContainSingle().Subject;
        incident.Severity.Should().Be(IncidentSeverity.Critical);
        incident.Category.Should().Be(IncidentCategory.Config);
        incident.Source.Should().Be(IncidentSource.Config);
        incident.RuleKind.Should().Be(RuleFailureTag.TaskRule);
        incident.RuleId.Should().Be(id);
        incident.ModuleKey.Should().Be(rig.Module);
        incident.TitleKey.Should().Be("ops.title.rulePaused");
        // 出廠收件人＝最後修改者＋全部系統管理員，各一則
        var expected = (await rig.AdminsAsync()).Append(RulePauseRig.Designer).Distinct().ToList();
        var notes = await rig.PauseNotificationsAsync();
        notes.Select(n => n.UserId).Should().BeEquivalentTo(expected);
        notes.Should().OnlyContain(n => n.Link == $"/system/dynamic-modules/{rig.Module}?tab=taskRules&rule={Rule}" && n.SourceId == incident.Id);
        var audit = (await rig.AuditAsync("taskRule.autoPause")).Should().ContainSingle().Subject;
        audit.ActorKind.Should().Be(AuditActorKind.System);
        audit.TargetId.Should().Be(Rule);
        audit.SummaryJson.Should().Contain("\"failures\":5").And.Contain("\"threshold\":5");

        var rule = await rig.RuleAsync(Rule);
        rule.GetProperty("autoPause").GetProperty("consecutiveFailures").GetInt32().Should().Be(5);
        rule.GetProperty("autoPause").GetProperty("lastErrorCode").GetString().Should().Be("InjectedFaultException");
        rule.TryGetProperty("failureStreak", out _).Should().BeFalse();
    }

    [Fact]
    public async Task E01_BeforePause_RuleListShowsFailureStreak()
    {
        using var rig = new RulePauseRig();
        await rig.BuildAsync();
        await rig.CreateRuleAsync(Rule);
        rig.Faults.Fail(Rule);
        await rig.FailCreatesAsync(2);

        var rule = await rig.RuleAsync(Rule);
        rule.GetProperty("failureStreak").GetProperty("consecutiveFailures").GetInt32().Should().Be(2);
        rule.TryGetProperty("autoPause", out _).Should().BeFalse();
    }

    [Fact]
    public async Task E02_WhilePaused_SaveSucceedsWithoutTaskAndCountsSkipped()
    {
        var (rig, id) = await Paused();
        using var owned = rig;
        var record = await rig.NewRecordAsync();
        (await rig.TaskCountAsync(id)).Should().Be(0);
        (await rig.HealthAsync(id))!.SkippedWhilePaused.Should().Be(1);
        await rig.NewRecordAsync();
        (await rig.HealthAsync(id))!.SkippedWhilePaused.Should().Be(2);
        (await rig.RuleAsync(Rule)).GetProperty("autoPause").GetProperty("skippedWhilePaused").GetInt32().Should().Be(2);
        (await rig.RecordTasksAsync(record)).GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task E02_WhilePaused_ManualRuleNotListed()
    {
        using var rig = new RulePauseRig();
        await rig.BuildAsync();
        await rig.CreateRuleAsync("r-manual", "manual");
        var record = await rig.NewRecordAsync();
        rig.Faults.Fail("r-manual");
        for (var i = 0; i < 5; i++)
        {
            rig.Tick();
            (await rig.Admin.PostAsync($"{RulePauseRig.Base}/records/{record}/task-rules/r-manual/apply", null)).StatusCode
                .Should().Be(HttpStatusCode.InternalServerError);
        }
        (await rig.RecordTasksAsync(record)).GetProperty("manualRules").EnumerateArray()
            .Should().NotContain(r => r.GetProperty("key").GetString() == "r-manual");
    }

    [Fact]
    public async Task E03_SuccessInBetween_RestartsCount()
    {
        using var rig = new RulePauseRig();
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        rig.Faults.Fail(Rule);
        await rig.FailCreatesAsync(3);
        rig.Faults.Heal(Rule);
        rig.Tick();
        await rig.NewRecordAsync();
        (await rig.HealthAsync(id))!.ConsecutiveFailures.Should().Be(0);
        rig.Faults.Fail(Rule);
        await rig.FailCreatesAsync(4);

        var h = (await rig.HealthAsync(id))!;
        h.PausedUtc.Should().BeNull();
        h.ConsecutiveFailures.Should().Be(4);
        (await rig.FailuresAsync(id)).Should().HaveCount(4, "成功之前的失敗列已清掉");
    }

    [Theory]
    [InlineData(new[] { 0, 59, 61, 62, 63, 64 }, 64, 5)]
    [InlineData(new[] { 0, 10, 20, 30, 70 }, null, 4)]
    public async Task E04_SlidingWindow(int[] minutes, int? pausedAt, int rowsLeft)
    {
        using var rig = new RulePauseRig();
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        rig.Faults.Fail(Rule);
        var start = rig.App.Time.GetUtcNow();
        foreach (var m in minutes)
        {
            OpsIncidentKit.SetTime(rig.App, start.AddMinutes(m));
            (await rig.PostRecordAsync()).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var paused = (await rig.HealthAsync(id))!.PausedUtc;
            if (m == pausedAt) paused.Should().NotBeNull($"第 {m} 分鐘往回 60 分鐘有 5 次");
            else paused.Should().BeNull($"第 {m} 分鐘還不該暫停");
        }
        var rows = await rig.FailuresAsync(id);
        rows.Should().HaveCount(rowsLeft);
        rows.Should().NotContain(f => f.FailedUtc < StorageTime.ToMicroseconds(start.AddMinutes(1).UtcDateTime), "窗口外的失敗列已清掉");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task E05_ConcurrentFourthAndFifth_PauseOnce(bool withoutWriteSerialization)
    {
        using var rig = new RulePauseRig(fileDatabase: true, withoutWriteSerialization: withoutWriteSerialization);
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        rig.Faults.Fail(Rule);
        await rig.FailCreatesAsync(3);
        rig.Tick();

        // 兩個請求都失敗、都停在補記之前，再同時放行：兩筆補記並行
        rig.Gate.Close();
        var first = rig.PostRecordAsync(client: rig.App.AsUser());
        await rig.Gate.WaitArrivalAsync();
        rig.Tick();
        var second = rig.PostRecordAsync(client: rig.App.AsUser());
        await rig.Gate.WaitArrivalAsync();
        rig.Gate.Open();
        var results = await Task.WhenAll(first, second);
        results.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.InternalServerError);

        var h = (await rig.HealthAsync(id))!;
        var errors = string.Join("\n", rig.App.Logs.Records.Where(r => r.Level >= LogLevel.Error).Select(r => r.Message));
        h.ConsecutiveFailures.Should().Be(5, errors);
        h.PausedUtc.Should().NotBeNull(errors);
        h.PauseCount.Should().Be(1);
        (await rig.IncidentsAsync(RuleErrorCodes.AutoPaused)).Should().ContainSingle();
        var notes = await rig.PauseNotificationsAsync();
        notes.Should().OnlyHaveUniqueItems(n => n.UserId);
        (await rig.AuditAsync("taskRule.autoPause")).Should().ContainSingle();
    }

    [Fact]
    public async Task E06_Resume_ResolvesIncident_NoBackfill_NewRecordsGetTasks()
    {
        var (rig, id) = await Paused();
        using var owned = rig;
        var r1 = await rig.NewRecordAsync();
        rig.Faults.Heal(Rule);

        rig.Tick();
        var res = await rig.ResumeAsync(Rule);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await JsonOf(res);
        body.TryGetProperty("autoPause", out _).Should().BeFalse();
        body.GetProperty("lastPause").GetProperty("skippedWhilePaused").GetInt32().Should().Be(1);

        var incident = (await rig.IncidentsAsync(RuleErrorCodes.AutoPaused)).Single();
        incident.Status.Should().Be(IncidentStatus.Resolved);
        incident.ResolvedReason.Should().Be(IncidentResolvedReason.RuleResumed);
        incident.ResolvedBy.Should().Be(RulePauseRig.Designer);
        var audit = (await rig.AuditAsync("taskRule.resume")).Should().ContainSingle().Subject;
        audit.ActorUserId.Should().Be(RulePauseRig.Designer);
        audit.SummaryJson.Should().Contain("\"skippedWhilePaused\":1");
        var h = (await rig.HealthAsync(id))!;
        (h.PausedUtc, h.ConsecutiveFailures, h.ResumedBy).Should().Be((null, 0, RulePauseRig.Designer));
        (await rig.FailuresAsync(id)).Should().BeEmpty();

        (await rig.PutRecordAsync(r1, new() { [rig.Note.Key] = "再存一次" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await rig.RecordTasksAsync(r1)).GetProperty("items").GetArrayLength().Should().Be(0, "暫停期間略過的不補產（O2-U7b）");
        var r2 = await rig.NewRecordAsync();
        (await rig.RecordTasksAsync(r2)).GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task E06b_UpdateMatchRule_AfterResume_NeedsNewEdge()
    {
        using var rig = new RulePauseRig();
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule, "onUpdateMatch", rig.FlagIsTrue);
        var records = new List<long>();
        for (var i = 0; i < 6; i++) records.Add(await rig.NewRecordAsync());
        rig.Faults.Fail(Rule);
        foreach (var r in records.Take(5))
        {
            rig.Tick();
            (await rig.PutRecordAsync(r, new() { [rig.Flag.Key] = true })).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
        (await rig.HealthAsync(id))!.PausedUtc.Should().NotBeNull();

        var r3 = records[5];
        (await rig.PutRecordAsync(r3, new() { [rig.Flag.Key] = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        rig.Faults.Heal(Rule);
        rig.Tick();
        (await rig.ResumeAsync(Rule)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await rig.PutRecordAsync(r3, new() { [rig.Note.Key] = "備註" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await rig.TaskCountAsync(id)).Should().Be(0, "條件早已成立，改備註不是新的成立");
        (await rig.PutRecordAsync(r3, new() { [rig.Flag.Key] = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await rig.PutRecordAsync(r3, new() { [rig.Flag.Key] = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await rig.TaskCountAsync(id)).Should().Be(1);
    }

    [Fact]
    public async Task E06c_ManualRule_HiddenWhilePaused_UsableAfterResume()
    {
        using var rig = new RulePauseRig();
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync("r-manual", "manual");
        var record = await rig.NewRecordAsync();
        rig.Faults.Fail("r-manual");
        string Apply() => $"{RulePauseRig.Base}/records/{record}/task-rules/r-manual/apply";
        for (var i = 0; i < 5; i++)
        {
            rig.Tick();
            (await rig.Admin.PostAsync(Apply(), null)).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
        (await rig.HealthAsync(id))!.PausedUtc.Should().NotBeNull();
        (await rig.Admin.PostAsync(Apply(), null)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        rig.Faults.Heal("r-manual");
        rig.Tick();
        (await rig.ResumeAsync("r-manual")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await rig.RecordTasksAsync(record)).GetProperty("manualRules").EnumerateArray()
            .Should().Contain(r => r.GetProperty("key").GetString() == "r-manual");
        var res = await rig.Admin.PostAsync(Apply(), null);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await rig.TaskCountAsync(id)).Should().Be(1);
    }

    [Fact]
    public async Task E07_ResumeWithFaultStill_PausesAgainAsRegression()
    {
        var (rig, id) = await Paused();
        using var owned = rig;
        rig.Tick();
        (await rig.ResumeAsync(Rule)).StatusCode.Should().Be(HttpStatusCode.OK);
        await rig.FailCreatesAsync(5);

        var h = (await rig.HealthAsync(id))!;
        h.PausedUtc.Should().NotBeNull();
        h.PauseCount.Should().Be(2);
        var incident = (await rig.IncidentsAsync(RuleErrorCodes.AutoPaused)).Should().ContainSingle().Subject;
        incident.Status.Should().NotBe(IncidentStatus.Resolved);
        incident.Regression.Should().BeTrue();
        incident.ReopenCount.Should().Be(1);
        (await rig.AuditAsync("taskRule.autoPause")).Should().HaveCount(2);
    }

    [Fact]
    public async Task E08_ReadOnlyDesigner_Forbidden_NotPaused_409()
    {
        var (rig, _) = await Paused();
        using var owned = rig;
        rig.GrantDesigner("sales01", edit: false);
        (await rig.ResumeAsync(Rule, rig.App.AsUser("sales01"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await rig.CreateRuleAsync("r-healthy");
        var res = await rig.ResumeAsync("r-healthy");
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(res)).GetProperty("code").GetString().Should().Be("ruleNotPaused");
        (await rig.ResumeAsync(Rule)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await rig.ResumeAsync(Rule)).StatusCode.Should().Be(HttpStatusCode.Conflict, "第二次按恢復時已經不是暫停");
    }

    private sealed class ThrowingWriteBack : IPipelineBehavior<RecordRuleFailureRequest, ServiceResult>
    {
        public Task<ServiceResult> Handle(RecordRuleFailureRequest request, RequestHandlerDelegate<ServiceResult> next, CancellationToken ct) =>
            throw new InvalidOperationException("補記壞了");
    }

    [Fact]
    public async Task E09_WriteBackThrows_UserStillGetsOriginal500_ErrorLogged()
    {
        using var rig = new RulePauseRig(extra: s => s.Insert(0,
            ServiceDescriptor.Transient<IPipelineBehavior<RecordRuleFailureRequest, ServiceResult>, ThrowingWriteBack>()));
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        rig.Faults.Fail(Rule);
        var res = await rig.PostRecordAsync();
        res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await rig.HealthAsync(id)).Should().BeNull();
        rig.App.Logs.Records.Should().ContainSingle(r => r.Level == LogLevel.Error && r.Category.Contains("RuleFailureBehavior")
            && r.Message.Contains("失敗計數"));
    }

    [Fact]
    public async Task E10_EventTooDeep_409_OpensConfigIncident_NoCount()
    {
        using var rig = new RulePauseRig();
        await rig.BuildAsync();
        var ruleId = await rig.CreateRuleAsync(Rule);
        // 七個模組串成六層連動（設計時不允許循環）：第 6 層超過上限 5，串聯過深
        var chain = new List<(string Module, string Title)> { (rig.Module, rig.Title) };
        for (var i = 0; i < 6; i++)
        {
            var m = await CreateModule(rig.Admin);
            await Activate(rig.Admin, m);
            rig.App.GrantBusinessGroups(m);
            chain.Add((m, await TitleKey(rig.Admin, m)));
        }
        for (var i = 0; i < 6; i++)
        {
            var ev = await rig.Planner.PostAsJsonAsync($"{RulePauseRig.Base}/events", new
            {
                key = $"ev-chain-{i}", name = new Dictionary<string, string> { ["zh-TW"] = $"串聯 {i}" }, sourceModuleKey = chain[i].Module, active = true,
                definition = new
                {
                    trigger = new { on = "create" },
                    effects = new[] { new { code = "next", kind = "create", targetModuleKey = chain[i + 1].Module,
                        set = new object[] { new { to = long.Parse(chain[i + 1].Title[2..]), @const = "下一層" } } } },
                },
            });
            ev.StatusCode.Should().Be(HttpStatusCode.Created, await ev.Content.ReadAsStringAsync());
        }

        var res = await rig.PostRecordAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await res.Content.ReadAsStringAsync());
        (await JsonOf(res)).GetProperty("code").GetString().Should().Be(RuleErrorCodes.EventTooDeep);
        var incident = (await rig.IncidentsAsync(RuleErrorCodes.EventTooDeep)).Should().ContainSingle().Subject;
        (incident.Source, incident.Category, incident.Severity, incident.RuleKind).Should()
            .Be((IncidentSource.Config, IncidentCategory.Config, IncidentSeverity.Warning, RuleFailureTag.Event));
        incident.TitleKey.Should().Be("ops.title.config.event.tooDeep");

        await rig.PostRecordAsync();
        (await rig.IncidentsAsync(RuleErrorCodes.EventTooDeep)).Should().ContainSingle().Which.OccurrenceCount.Should().Be(2);
        (await rig.HealthAsync(ruleId)).Should().BeNull("已知錯誤碼不計數、不暫停");
        await using var scope = rig.App.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<RuleHealth>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task E11_RuleEditorOnly_EditorInactive_FallsBackToAdmins()
    {
        using var rig = new RulePauseRig();
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        var put = await rig.Admin.PutAsJsonAsync($"{RulePauseRig.Base}/system/policies/ops.configIssueRecipients", new { value = new[] { "ruleEditor" } });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        await using (var scope = rig.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            await db.Set<AppUser>().Where(u => u.UserId == RulePauseRig.Designer).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }
        rig.Faults.Fail(Rule);
        await rig.FailCreatesAsync(5);

        (await rig.HealthAsync(id))!.PausedUtc.Should().NotBeNull();
        (await rig.PauseNotificationsAsync()).Select(n => n.UserId).Should().BeEquivalentTo(await rig.AdminsAsync());
        rig.App.Logs.Records.Should().Contain(r => r.Level == LogLevel.Warning && r.Message.Contains("ops.configIssueRecipients"));
    }

    private sealed record Outer(string ModuleKey, Dictionary<string, System.Text.Json.JsonElement?> Values) : IRequest<ServiceResult>;

    private sealed class OuterHandler(ISender sender) : IRequestHandler<Outer, ServiceResult>
    {
        public Task<ServiceResult> Handle(Outer request, CancellationToken ct) =>
            sender.Send(new Application.Dynamic.RecordService.Commands.CreateRecord.CreateRecordRequest(request.ModuleKey, request.Values), ct);
    }

    [Fact]
    public async Task E12_NestedRequests_CountOnce()
    {
        using var rig = new RulePauseRig(extra: s => s.AddTransient<IRequestHandler<Outer, ServiceResult>, OuterHandler>());
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        rig.Faults.Fail(Rule);
        var values = new Dictionary<string, System.Text.Json.JsonElement?>
        {
            [rig.Title] = System.Text.Json.JsonSerializer.SerializeToElement("案"),
            [rig.Owner.Key] = System.Text.Json.JsonSerializer.SerializeToElement("sales02"),
        };
        await using (var scope = rig.App.Services.CreateAsyncScope())
        {
            using var acting = Application.Common.Acting.ActingScope.Begin("admin");
            var act = () => scope.ServiceProvider.GetRequiredService<ISender>().Send(new Outer(rig.Module, values));
            await act.Should().ThrowAsync<InjectedFaultException>();
        }
        var h = (await rig.HealthAsync(id))!;
        h.ConsecutiveFailures.Should().Be(1);
        (await rig.FailuresAsync(id)).Should().ContainSingle();
    }

    [Fact]
    public async Task E14_AfterResume_OldFailuresGone_FifthNewFailurePausesAgain()
    {
        var (rig, id) = await Paused();
        using var owned = rig;
        rig.Tick();
        (await rig.ResumeAsync(Rule)).StatusCode.Should().Be(HttpStatusCode.OK);
        await rig.FailCreatesAsync(1);
        (await rig.HealthAsync(id))!.PausedUtc.Should().BeNull("恢復時刪掉暫停前的失敗列");
        await rig.FailCreatesAsync(3);
        (await rig.HealthAsync(id))!.PausedUtc.Should().BeNull();
        await rig.FailCreatesAsync(1);
        (await rig.HealthAsync(id))!.PausedUtc.Should().NotBeNull();
        (await rig.IncidentsAsync(RuleErrorCodes.AutoPaused)).Single().Regression.Should().BeTrue();
    }

    /// <summary>E15、E16：P 在 t 失敗、停在補記之前；Q 在 t+1 評估同一條規則成功並提交；再放行 P。</summary>
    private static async Task FailThenSucceedBeforeWriteBack(RulePauseRig rig)
    {
        rig.Tick();
        rig.Gate.Close();
        var p = rig.PostRecordAsync(client: rig.App.AsUser());
        await rig.Gate.WaitArrivalAsync();
        rig.Faults.Heal(Rule);
        rig.Tick();
        (await rig.PostRecordAsync()).StatusCode.Should().Be(HttpStatusCode.Created);
        rig.Gate.Open();
        (await p).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        rig.Faults.Fail(Rule);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task E15_SuccessCommittedBeforeEarlierFailureWriteBack_DoesNotCount(bool withoutWriteSerialization)
    {
        using var rig = new RulePauseRig(fileDatabase: true, withoutWriteSerialization: withoutWriteSerialization);
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        rig.Faults.Fail(Rule);
        await rig.FailCreatesAsync(4);
        var t5 = StorageTime.ToMicroseconds(rig.App.Time.GetUtcNow().UtcDateTime.AddSeconds(1));
        await FailThenSucceedBeforeWriteBack(rig);

        var h = (await rig.HealthAsync(id))!;
        h.StreakResetUtc.Should().Be(t5.AddSeconds(1), "Q 的評估時間 t6");
        h.ConsecutiveFailures.Should().Be(0);
        h.LastFailureUtc.Should().Be(t5, "P 的失敗時間是評估時間，不是補記時間");
        (await rig.FailuresAsync(id)).Should().NotContain(f => f.FailedUtc > t5.AddSeconds(-1), "P 不新增失敗列（之前的舊列等下次補記再清）");

        await rig.FailCreatesAsync(4);
        (await rig.HealthAsync(id))!.PausedUtc.Should().BeNull();
        await rig.FailCreatesAsync(1);
        (await rig.HealthAsync(id))!.PausedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task E16_NoHealthRow_InFlightFailure_SuccessCreatesRow()
    {
        using var rig = new RulePauseRig(fileDatabase: true);
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        rig.Faults.Fail(Rule);
        var t1 = StorageTime.ToMicroseconds(rig.App.Time.GetUtcNow().UtcDateTime.AddSeconds(1));
        await FailThenSucceedBeforeWriteBack(rig);

        var h = (await rig.HealthAsync(id))!;
        (h.ConsecutiveFailures, h.StreakResetUtc).Should().Be((0, t1.AddSeconds(1)));
        await rig.FailCreatesAsync(4);
        (await rig.HealthAsync(id))!.PausedUtc.Should().BeNull();
        (await rig.HealthAsync(id))!.ConsecutiveFailures.Should().Be(4);
    }

    [Fact]
    public async Task E16_Control_SuccessWithoutInFlight_WritesNothing()
    {
        using var rig = new RulePauseRig();
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        await rig.NewRecordAsync();
        (await rig.HealthAsync(id)).Should().BeNull();
    }

    /// <summary>暫停那一步寫事件時丟例外：整個補記交易回滾（不留半套的暫停），下一次失敗補記重新數到門檻而暫停。</summary>
    private sealed class FailIncidentInsertOnce : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public volatile bool Armed;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken ct = default)
        {
            if (Armed && command.CommandText.Contains("INSERT INTO \"Dynamic_OpsIncident\" ", StringComparison.Ordinal))
            {
                Armed = false;
                throw new InvalidOperationException("寫事件失敗（測試）");
            }
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }
    }

    [Fact]
    public async Task WriteBackRolledBack_ThenNextFailureRetriesPause()
    {
        var fail = new FailIncidentInsertOnce();
        using var rig = new RulePauseRig(interceptor: fail);
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        rig.Faults.Fail(Rule);
        await rig.FailCreatesAsync(4);
        fail.Armed = true;
        await rig.FailCreatesAsync(1);

        var h = (await rig.HealthAsync(id))!;
        (h.PausedUtc, h.ConsecutiveFailures, h.PauseCount).Should().Be((null, 4, 0), "第 5 次的補記整個回滾");
        (await rig.IncidentsAsync(RuleErrorCodes.AutoPaused)).Should().BeEmpty();
        (await rig.AuditAsync("taskRule.autoPause")).Should().BeEmpty();
        rig.App.Logs.Records.Should().Contain(r => r.Level == LogLevel.Error && r.Message.Contains("失敗計數"));

        await rig.FailCreatesAsync(1);
        h = (await rig.HealthAsync(id))!;
        (h.PausedUtc is null, h.ConsecutiveFailures, h.PauseCount).Should().Be((false, 5, 1));
        (await rig.IncidentsAsync(RuleErrorCodes.AutoPaused)).Should().ContainSingle();
    }

    [Fact]
    public async Task TwoInstances_FailuresAddUp_PauseOnce_ResumeOnce()
    {
        using var a = new RulePauseRig(fileDatabase: true);
        await a.BuildAsync();
        var id = await a.CreateRuleAsync(Rule);
        using var b = a.SecondInstance();
        a.Faults.Fail(Rule);
        b.Faults.Fail(Rule);
        b.App.Time.Advance(a.App.Time.GetUtcNow() - b.App.Time.GetUtcNow());
        await a.FailCreatesAsync(2);
        b.Tick(10);
        await b.FailCreatesAsync(2);
        a.Tick(20);
        await Task.WhenAll(a.FailCreatesAsync(1), b.FailCreatesAsync(1));

        var h = (await a.HealthAsync(id))!;
        h.PausedUtc.Should().NotBeNull();
        h.PauseCount.Should().Be(1);
        (await a.IncidentsAsync(RuleErrorCodes.AutoPaused)).Should().ContainSingle();
        (await b.NewRecordAsync()).Should().BePositive("另一台讀到暫停，存檔成功而略過");
        (await a.HealthAsync(id))!.SkippedWhilePaused.Should().Be(1);

        var results = await Task.WhenAll(a.ResumeAsync(Rule), b.ResumeAsync(Rule));
        results.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await a.AuditAsync("taskRule.resume")).Should().ContainSingle();
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public void FaultInjection_OnlyInDevelopment_WarnsEitherWay(string environment, bool injects)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITaskRuleFaultInjector, NoTaskRuleFaults>();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [WebAPI.Ops.TaskRuleFaultInjection.Key] = "r-a, r-b" }).Build();
        var env = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = environment };
        var warnings = new List<string>();
        WebAPI.Ops.TaskRuleFaultInjection.Configure(services, config, env, warnings);

        var injector = services.BuildServiceProvider().GetRequiredService<ITaskRuleFaultInjector>();
        warnings.Should().ContainSingle().Which.Should().Contain(WebAPI.Ops.TaskRuleFaultInjection.Key);
        if (!injects)
        {
            injector.Should().BeOfType<NoTaskRuleFaults>();
            return;
        }
        var act = () => injector.Inject(new TaskRule { Key = "r-b" });
        act.Should().Throw<InjectedFaultException>();
        injector.Inject(new TaskRule { Key = "r-c" });
    }

    [Fact]
    public async Task DeleteRule_RemovesHealth_ResolvesIncidentAsRuleDeleted()
    {
        var (rig, id) = await Paused();
        using var owned = rig;
        (await rig.Planner.DeleteAsync($"{RulePauseRig.Base}/modules/{rig.Module}/task-rules/{Rule}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await rig.HealthAsync(id)).Should().BeNull();
        (await rig.FailuresAsync(id)).Should().BeEmpty();
        (await rig.IncidentsAsync(RuleErrorCodes.AutoPaused)).Single().ResolvedReason.Should().Be(IncidentResolvedReason.RuleDeleted);
    }

    [Fact]
    public async Task F01_Upkeep_PurgesLeftoverRuleFailures()
    {
        using var rig = new RulePauseRig();
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);
        rig.Faults.Fail(Rule);
        await rig.FailCreatesAsync(2);
        (await rig.FailuresAsync(id)).Should().HaveCount(2);

        // 一天沒再失敗：超過窗口上限（1440 分鐘）
        rig.App.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        var clock = new ManualClock(rig.App.Time.GetUtcNow());
        var runner = Runner(rig.App, clock, rig.App.Services.GetServices<IScheduledJob>().Where(j => j.Key == "ops.upkeep"));
        await runner.EnsureJobsAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(61));
        await runner.TickAsync(CancellationToken.None);

        (await Row(rig.App, "ops.upkeep")).LastStatus.Should().Be(ScheduledJobStatus.Succeeded);
        (await rig.FailuresAsync(id)).Should().BeEmpty();
        (await rig.HealthAsync(id))!.ConsecutiveFailures.Should().Be(2, "健康列不清，只清失敗列");
        (await rig.AuditAsync("ops.retention.purge")).Should().Contain(a => a.SummaryJson!.Contains("\"ruleFailure\":2"));
    }
}
