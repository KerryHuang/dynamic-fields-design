using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 流程圖的同意後推進與每次同意的重新比對（計畫 fg2 Task 5；流程圖設計 §8.2、§8.7）。
/// 簽核人：a＝fin1、b＝buyer2、c＝gm、z＝emp1；發起人 buyer1。
/// </summary>
public class ApprovalGraphApproveTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    // ───────── 圖與情境 ─────────

    /// <summary>start → a → b → end。</summary>
    private static ApprovalDefinition Linear2() => G(
        [Start(), Approval("a", Users("fin1")), Approval("b", Users("buyer2")), End()],
        [E("e0", "start", "a"), E("ab", "a", "b"), E("be", "b", "end")]);

    /// <summary>start → fork f → 分支 a、b → join j → c → end；分支出線依 fa、fb 順序。</summary>
    private static ApprovalDefinition ForkJoin(string joinMode, ApproverSource? a = null, ApproverSource? b = null, string aMode = ApprovalModes.Any) => G(
        [Start(), Fork("f"), Approval("a", a ?? Users("fin1"), mode: aMode), Approval("b", b ?? Users("buyer2")), Join("j", "f", joinMode),
         Approval("c", Users("gm")), End()],
        [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("jc", "j", "c"), E("ce", "c", "end")]);

    /// <summary>fork f 的兩條分支：a（簽核）與空分支（f → j 直連）。</summary>
    private static ApprovalDefinition EmptyArm(string joinMode) => G(
        [Start(), Fork("f"), Approval("a", Users("fin1")), Join("j", "f", joinMode), Approval("c", Users("gm")), End()],
        [E("e0", "start", "f"), E("fa", "f", "a"), E("fe", "f", "j"), E("aj", "a", "j"), E("jc", "j", "c"), E("ce", "c", "end")]);

    /// <summary>外層 f（outer）：分支 fx 是簽核 x，分支 ff2 是內層 f2（匯合 j2，模式 inner）下的 y、z。</summary>
    private static ApprovalDefinition Nested(string outer, string inner) => G(
        [Start(), Fork("f"), Approval("x", Users("fin1")), Fork("f2"), Approval("y", Users("buyer2")), Approval("z", Users("emp1")),
         Join("j2", "f2", inner), Join("j", "f", outer), End()],
        [E("e0", "start", "f"), E("fx", "f", "x"), E("ff2", "f", "f2"), E("f2y", "f2", "y"), E("f2z", "f2", "z"),
         E("yj2", "y", "j2"), E("zj2", "z", "j2"), E("j2j", "j2", "j"), E("xj", "x", "j"), E("je", "j", "end")]);

    /// <summary>條件節點 c：總額 ≥ 100000 走 a（fin1），預設線走 b（buyer2）；兩個簽核都接 end。</summary>
    private static ApprovalDefinition CondAb(PurchaseRig r) => G(
        [Start(), Condition("c"), Approval("a", Users("fin1")), Approval("b", Users("buyer2")), End()],
        [E("e0", "start", "c"), E("ca", "c", "a", Cond(r.Total, "gte", 100000), order: 1), E("cb", "c", "b", isDefault: true),
         E("ae", "a", "end"), E("be", "b", "end")]);

    /// <summary>
    /// 任一到齊的 f：分支一是 a（fin1），分支二有條件節點 c（總額 ≥ 100000 走 b2 emp1，預設走 b3 buyer2）；匯合 j 之後是 d（gm）。
    /// emptySecond 為 true 時，分支二換成空分支（f → j 直連），條件節點移到分支一（排在前面展開）。
    /// </summary>
    private static ApprovalDefinition AnyWithCondition(PurchaseRig r, bool emptySecond) => emptySecond
        ? G([Start(), Fork("f"), Condition("c"), Approval("a", Users("fin1")), Approval("a2", Users("buyer2")), Join("j", "f", ApprovalModes.Any), Approval("d", Users("gm")), End()],
            [E("e0", "start", "f"), E("fa", "f", "c"), E("fe", "f", "j"), E("ca", "c", "a", Cond(r.Total, "gte", 100000), order: 1),
             E("cd", "c", "a2", isDefault: true), E("aj", "a", "j"), E("a2j", "a2", "j"), E("jd", "j", "d"), E("de", "d", "end")])
        : G([Start(), Fork("f"), Approval("a", Users("fin1")), Condition("c"), Approval("b2", Users("emp1")), Approval("b3", Users("buyer2")),
             Join("j", "f", ApprovalModes.Any), Approval("d", Users("gm")), End()],
            [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "c"), E("ca", "c", "b2", Cond(r.Total, "gte", 100000), order: 1),
             E("cd", "c", "b3", isDefault: true), E("aj", "a", "j"), E("b2j", "b2", "j"), E("b3j", "b3", "j"), E("jd", "j", "d"), E("de", "d", "end")]);

    private async Task<PurchaseRig> RigWith(Func<PurchaseRig, Task> seed, bool grantEveryone = true)
    {
        var r = await BuildPurchase();
        await seed(r);
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        if (grantEveryone) await GrantEveryone(r.Module, r.Extra);
        return r;
    }

    private Task<PurchaseRig> Rig(Func<PurchaseRig, ApprovalDefinition> graph) => RigWith(r => SeedGraphFlow(r, "po-g", r.Confirmed, 1, graph(r)));

    private Task<PurchaseRig> Rig(ApprovalDefinition graph) => Rig(_ => graph);

    private async Task<(long Po, long Inst)> SubmitAs(PurchaseRig r, decimal price = 5000, string user = "buyer1")
    {
        As(user);
        var po = await NewPo(r, price: price);
        return (po.Id, (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value);
    }

    /// <summary>簽核中由其他模組（追加費用）讓彙總變動：允許的靜默更新（設計 §8.5）。</summary>
    private async Task AddExtra(PurchaseRig r, long poId, decimal amount)
    {
        As("buyer1");
        var res = await Send(new CreateRecordRequest(r.Extra, V((TitleId(r.Extra), "追加"), (r.ExtraRef, poId), (r.ExtraAmount, amount))));
        res.IsCreated.Should().BeTrue(Dump(res));
    }

    private List<ApprovalStageRun> RunsOf(long id) =>
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == id).OrderBy(x => x.Seq).ToList();

    private List<ApprovalStageRun> NodeRuns(long id, string nodeId) => RunsOf(id).Where(x => x.NodeId == nodeId).ToList();

    private ApprovalStageRun RunOf(long id, string nodeId) => NodeRuns(id, nodeId).Single();

    private ApprovalTask Open(long id, string user) => TasksOf(id).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private Task<ServiceResult> ApproveTask(long id, long taskId, string user, Dictionary<string, JsonElement?>? values = null)
    {
        As(user);
        return Send(new ApproveTaskRequest(taskId, Inst(id).RowVersion, null, values));
    }

    private Task<ServiceResult> Approve(long id, string user, Dictionary<string, JsonElement?>? values = null) => ApproveTask(id, Open(id, user).Id, user, values);

    private async Task MustApprove(long id, string user)
    {
        var res = await Approve(id, user);
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private static ApprovalActionResponse Body(ServiceResult r) => (ApprovalActionResponse)r.Data!;

    private int Count(long id, string action) => ActionsOf(id).Count(a => a.Action == action);

    private ApprovalInstance? PendingOf(long recordId) =>
        Db.Context.Set<ApprovalInstance>().AsNoTracking().SingleOrDefault(i => i.RecordId == recordId && i.Status == ApprovalInstanceStatus.Pending);

    // ───────── 沿出線推進 ─────────

    [Fact]
    public async Task Approve_Linear_AdvancesToNextNode()
    {
        var r = await Rig(Linear2());
        var (_, id) = await SubmitAs(r);

        var res = await Approve(id, "fin1");

        res.IsSuccess.Should().BeTrue(Dump(res));
        Body(res).Outcome.Should().Be(ApprovalOutcomes.Advanced);
        RunsOf(id).Select(x => (x.NodeId, x.Status)).Should().Equal(("start", "passed"), ("a", "approved"), ("b", "active"));
        RunOf(id, "b").Should().Match<ApprovalStageRun>(x => x.InEdgeId == "ab" && x.BranchKey == "");
        TasksOf(id).Select(t => (t.AssigneeUserId, t.Status)).Should().Equal(("fin1", ApprovalTaskStatus.Approved), ("buyer2", ApprovalTaskStatus.Open));
        Inst(id).CurrentStageRunId.Should().Be(RunOf(id, "b").Id);
        Notices("buyer2", NotificationKinds.ApprovalPending).Should().Be(1);
    }

    [Fact]
    public async Task Approve_LastNode_EndPassed_InstanceApproved_TransitionRuns()
    {
        var r = await Rig(Linear2());
        var (po, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");

        var res = await Approve(id, "buyer2");

        res.IsSuccess.Should().BeTrue(Dump(res));
        Body(res).Outcome.Should().Be(ApprovalOutcomes.Approved);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        Inst(id).CurrentStageRunId.Should().BeNull();
        StateOf(r, po).Should().Be(r.Confirmed);
        RunsOf(id).Last().Should().Match<ApprovalStageRun>(x => x.NodeId == "end" && x.Status == "passed" && x.InEdgeId == "be");
        Count(id, ApprovalActions.Complete).Should().Be(1);
    }

    // ───────── 匯合 ─────────

    [Fact]
    public async Task AllJoin_FirstBranchWaits_SecondCompletes_ContinuesOnce()
    {
        var r = await Rig(ForkJoin(ApprovalModes.All));
        var (_, id) = await SubmitAs(r);

        await MustApprove(id, "fin1");
        NodeRuns(id, "j").Select(x => x.Status).Should().Equal("waiting");
        NodeRuns(id, "c").Should().BeEmpty();

        var res = await Approve(id, "buyer2");

        res.IsSuccess.Should().BeTrue(Dump(res));
        NodeRuns(id, "j").Select(x => x.Status).Should().Equal("passed", "passed");
        NodeRuns(id, "c").Should().ContainSingle().Which.Status.Should().Be(ApprovalStageStatus.Active);
        Open(id, "gm").Should().NotBeNull();
    }

    [Fact]
    public async Task AllJoin_ArrivalRowPerBranch_InEdgeIds()
    {
        var r = await Rig(ForkJoin(ApprovalModes.All));
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");
        await MustApprove(id, "buyer2");

        NodeRuns(id, "j").Select(x => (x.InEdgeId, x.BranchKey, x.NodeKind)).Should().Equal(("aj", "", "join"), ("bj", "", "join"));
        RunOf(id, "c").InEdgeId.Should().Be("jc");
    }

    [Fact]
    public async Task AllJoin_EmptyBranchPlusApprovalBranch_ContinuesAfterApproval()
    {
        var r = await Rig(EmptyArm(ApprovalModes.All));
        var (_, id) = await SubmitAs(r);
        NodeRuns(id, "j").Should().ContainSingle().Which.Should().Match<ApprovalStageRun>(x => x.InEdgeId == "fe" && x.Status == "waiting");

        await MustApprove(id, "fin1");

        NodeRuns(id, "j").Select(x => (x.InEdgeId, x.Status)).Should().Equal(("fe", "passed"), ("aj", "passed"));
        RunOf(id, "c").Status.Should().Be(ApprovalStageStatus.Active);
    }

    [Fact]
    public async Task AnyJoin_FirstArrival_OthersClosedBranchCancelled_TasksClosed_HistoryWritten()
    {
        var r = await Rig(ForkJoin(ApprovalModes.Any));
        var (_, id) = await SubmitAs(r);

        await MustApprove(id, "buyer2");

        RunOf(id, "a").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.BranchCancelled);
        TasksOf(id).Single(t => t.AssigneeUserId == "fin1").Status.Should().Be(ApprovalTaskStatus.Closed);
        RunOf(id, "j").Status.Should().Be(ApprovalStageStatus.Passed);
        RunOf(id, "c").Status.Should().Be(ApprovalStageStatus.Active);
        var cancelled = ActionsOf(id).Single(a => a.Action == ApprovalActions.BranchCancelled);
        using var data = JsonDocument.Parse(cancelled.DataJson!);
        data.RootElement.GetProperty("forkId").GetString().Should().Be("f");
        data.RootElement.GetProperty("joinId").GetString().Should().Be("j");
        data.RootElement.GetProperty("arrived").GetString().Should().Be("fb");
        data.RootElement.GetProperty("cancelled").EnumerateArray().Select(e => e.GetString()).Should().Equal("fa");
    }

    [Fact]
    public async Task AnyJoin_CancelledTaskLosesReadGrant()
    {
        // 只有 buyers 群組有模組權限：fin1 靠任務取得讀取權，任務被取消後就沒有（比照 ApprovalReadGrantTests）
        var r = await RigWith(x => SeedGraphFlow(x, "po-g", x.Confirmed, 1, ForkJoin(ApprovalModes.Any)), grantEveryone: false);
        var buyers = new PermissionGroup { Key = "buyers", NameI18n = Text("buyers") };
        Db.Context.Add(buyers);
        await Db.Context.SaveChangesAsync();
        Db.Context.AddRange(new UserGroupMapping { GroupId = buyers.Id, UserId = "buyer1" }, new UserGroupMapping { GroupId = buyers.Id, UserId = "buyer2" });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        As("admin");
        (await Send(new SaveModulePermissionsRequest(r.Module, new ModulePermissionsDto(
            [new GroupPermissionDto(buyers.Id, new PrivilegesDto(true, true, true, true, true, true, false), DataScope.All)], [], [], []))))
            .IsSuccess.Should().BeTrue();
        var (po, id) = await SubmitAs(r);
        As("fin1");
        (await Send(new GetRecordRequest(po))).IsSuccess.Should().BeTrue();

        await MustApprove(id, "buyer2");

        As("fin1");
        (await Send(new GetRecordRequest(po))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task AnyJoin_CancelledAssignees_NoNotice()
    {
        var r = await Rig(ForkJoin(ApprovalModes.Any));
        var (_, id) = await SubmitAs(r);

        await MustApprove(id, "buyer2");

        // 被取消分支的負責人只有送出時那則待簽，之後沒有收到任何通知（設計 U5）
        Db.Context.Set<Notification>().AsNoTracking().Count(n => n.UserId == "fin1").Should().Be(1);
        Notices("fin1", NotificationKinds.ApprovalPending).Should().Be(1);
    }

    [Fact]
    public async Task AnyJoin_NestedForkInCancelledBranch_AllClosed()
    {
        // 外層任一到齊、內層全部到齊：內層已有一邊簽過、另一邊還在簽時，外層另一條分支先到，內層整段（含匯合的等待列）都關閉
        var r = await Rig(Nested(ApprovalModes.Any, ApprovalModes.All));
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "buyer2");
        NodeRuns(id, "j2").Should().ContainSingle().Which.Status.Should().Be(ApprovalStageStatus.Waiting);

        await MustApprove(id, "fin1");

        RunOf(id, "z").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.BranchCancelled);
        RunOf(id, "j2").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.BranchCancelled);
        RunOf(id, "y").Status.Should().Be(ApprovalStageStatus.Approved);
        TasksOf(id).Single(t => t.AssigneeUserId == "emp1").Status.Should().Be(ApprovalTaskStatus.Closed);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
    }

    [Fact]
    public async Task Approve_AllMode_InBranch_WaitsEverySeat()
    {
        var r = await Rig(ForkJoin(ApprovalModes.All, a: Group(FinanceGroup), aMode: ApprovalModes.All));
        var (_, id) = await SubmitAs(r);
        TasksOf(id).Where(t => t.AssigneeUserId is "fin1" or "fin2").Should().HaveCount(2);

        await MustApprove(id, "fin1");
        RunOf(id, "a").Status.Should().Be(ApprovalStageStatus.Active);
        NodeRuns(id, "j").Should().BeEmpty();

        await MustApprove(id, "fin2");
        RunOf(id, "a").Status.Should().Be(ApprovalStageStatus.Approved);
        NodeRuns(id, "j").Select(x => x.Status).Should().Equal("waiting");

        await MustApprove(id, "buyer2");
        RunOf(id, "c").Status.Should().Be(ApprovalStageStatus.Active);
    }

    [Fact]
    public async Task SamePersonInTwoBranches_EachTaskSignedSeparately()
    {
        var r = await Rig(ForkJoin(ApprovalModes.All, a: Users("fin1"), b: Users("fin1")));
        var (_, id) = await SubmitAs(r);
        var tasks = TasksOf(id);
        tasks.Should().HaveCount(2).And.OnlyContain(t => t.AssigneeUserId == "fin1");

        (await ApproveTask(id, tasks[0].Id, "fin1")).IsSuccess.Should().BeTrue();
        TasksOf(id).Single(t => t.Id == tasks[1].Id).Status.Should().Be(ApprovalTaskStatus.Open);
        NodeRuns(id, "c").Should().BeEmpty();

        var second = await ApproveTask(id, tasks[1].Id, "fin1");

        second.IsSuccess.Should().BeTrue(Dump(second));
        RunOf(id, "c").Status.Should().Be(ApprovalStageStatus.Active);
    }

    // ───────── 每次同意的重新比對（§8.7）─────────

    [Fact]
    public async Task Recheck_ConditionEdgeChanged_Rerouted_NewInstanceFromStart()
    {
        var r = await Rig(CondAb);
        var (po, id) = await SubmitAs(r, price: 50000);
        RunOf(id, "b").Status.Should().Be(ApprovalStageStatus.Active);
        await AddExtra(r, po, 120000);

        var res = await Approve(id, "buyer2");

        res.IsSuccess.Should().BeTrue(Dump(res));
        Body(res).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Rerouted);
        TasksOf(id).Should().NotContain(t => t.Status == ApprovalTaskStatus.Approved);   // 這次同意不計入
        var fresh = PendingOf(po)!;
        fresh.RestartedFromInstanceId.Should().Be(id);
        RunsOf(fresh.Id).Select(x => (x.NodeId, x.Status)).Should().Equal(("start", "passed"), ("c", "passed"), ("a", "active"));
        Open(fresh.Id, "fin1").Should().NotBeNull();
        Body(res).InstanceId.Should().Be(fresh.Id);
    }

    [Fact]
    public async Task Recheck_SkippedNodeNowHolds_Rerouted()
    {
        var r = await Rig(r0 => G([Start(), Approval("a", Users("fin1"), when: Cond(r0.Total, "gt", 50000)), Approval("b", Users("buyer2")), End()],
            [E("e0", "start", "a"), E("ab", "a", "b"), E("be", "b", "end")]));
        var (po, id) = await SubmitAs(r, price: 40000);
        RunOf(id, "a").Status.Should().Be(ApprovalStageStatus.Skipped);
        await AddExtra(r, po, 20000);

        var res = await Approve(id, "buyer2");

        Body(res).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        var fresh = PendingOf(po)!;
        RunOf(fresh.Id, "a").Status.Should().Be(ApprovalStageStatus.Active);
        Open(fresh.Id, "fin1").Should().NotBeNull();
    }

    [Fact]
    public async Task Recheck_CancelledBranchChange_NotRerouted()
    {
        // 任一到齊通過後，被取消的分支上的條件節點就算改選也不重來（§8.7 第 4 點）
        var r = await Rig(r0 => AnyWithCondition(r0, emptySecond: false));
        var (po, id) = await SubmitAs(r, price: 50000);
        await MustApprove(id, "fin1");
        RunOf(id, "b3").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.BranchCancelled);
        await AddExtra(r, po, 120000);

        var res = await Approve(id, "gm");

        res.IsSuccess.Should().BeTrue(Dump(res));
        Body(res).Outcome.Should().Be(ApprovalOutcomes.Approved);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
    }

    [Fact]
    public async Task Recheck_EmptyBranchAnyPassed_CancelledBranchChange_NotRerouted()
    {
        // 送出當下空分支讓任一到齊通過，先展開的條件分支被取消；之後總額改變也不重來
        var r = await Rig(r0 => AnyWithCondition(r0, emptySecond: true));
        var (po, id) = await SubmitAs(r, price: 50000);
        RunOf(id, "a2").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.BranchCancelled);
        await AddExtra(r, po, 120000);

        var res = await Approve(id, "gm");

        Body(res).Outcome.Should().Be(ApprovalOutcomes.Approved);
    }

    [Fact]
    public async Task Recheck_NestedJoinConditionChanged_Rerouted()
    {
        // 內層分支裡的條件節點改選另一條出線：巢狀的分支段也要比
        var r = await Rig(r0 => G(
            [Start(), Fork("f"), Approval("x", Users("fin1")), Fork("f2"), Condition("cy"), Approval("y1", Users("buyer2")), Approval("y2", Users("emp2")),
             Approval("z", Users("emp1")), Join("j2", "f2"), Join("j", "f"), End()],
            [E("e0", "start", "f"), E("fx", "f", "x"), E("ff2", "f", "f2"), E("f2y", "f2", "cy"), E("f2z", "f2", "z"),
             E("cy2", "cy", "y2", Cond(r0.Total, "gte", 100000), order: 1), E("cy1", "cy", "y1", isDefault: true),
             E("y1j2", "y1", "j2"), E("y2j2", "y2", "j2"), E("zj2", "z", "j2"), E("j2j", "j2", "j"), E("xj", "x", "j"), E("je", "j", "end")]));
        var (po, id) = await SubmitAs(r, price: 50000);
        RunOf(id, "y1").Status.Should().Be(ApprovalStageStatus.Active);
        await AddExtra(r, po, 120000);

        var res = await Approve(id, "emp1");

        Body(res).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        var fresh = PendingOf(po)!;
        RunOf(fresh.Id, "y2").Status.Should().Be(ApprovalStageStatus.Active);
        Open(fresh.Id, "emp2").Should().NotBeNull();
    }

    private static ApprovalDefinition MemoCondition(PurchaseRig r) => G(
        [Start(), Condition("c"), Approval("a", Users("fin1")), Approval("b", Users("buyer2")), End()],
        [E("e0", "start", "c"), E("ca", "c", "a", Cond(r.Memo, "eq", "x"), order: 1), E("cb", "c", "b", isDefault: true), E("ae", "a", "end"), E("be", "b", "end")]);

    /// <summary>欄位停用後，引用它的條件解析不了（引用的欄位已不在可用狀態）。</summary>
    private async Task DeactivateField(long fieldId)
    {
        await Db.Context.Set<Field>().Where(f => f.Id == fieldId).ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, FieldStatus.Inactive));
        Db.Context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Recheck_ConditionNodeUndecidable_409_AlertWritten_NothingWritten()
    {
        var r = await Rig(MemoCondition);
        var (_, id) = await SubmitAs(r);
        RunOf(id, "b").Status.Should().Be(ApprovalStageStatus.Active);
        await DeactivateField(r.Memo);
        var before = (Inst(id).RowVersion, ActionsOf(id).Count, RunsOf(id).Count);

        var res = await Approve(id, "buyer2");

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.ConditionNodeUndecidable("c").Resolve(null));
        (Inst(id).RowVersion, ActionsOf(id).Count, RunsOf(id).Count).Should().Be(before);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        Open(id, "buyer2").Status.Should().Be(ApprovalTaskStatus.Open);
        Notices("admin", NotificationKinds.ApprovalAdminAlert).Should().Be(1);
    }

    [Fact]
    public async Task Recheck_SkippedNodeConditionUnparseable_Rerouted()
    {
        // 略過條件引用的欄位已不在：視為成立、不略過（同第一版 Holds）→ 路線變了，重來
        var r = await Rig(r0 => G([Start(), Approval("a", Users("fin1"), when: Cond(r0.Memo, "eq", "x")), Approval("b", Users("buyer2")), End()],
            [E("e0", "start", "a"), E("ab", "a", "b"), E("be", "b", "end")]));
        var (po, id) = await SubmitAs(r);
        RunOf(id, "a").Status.Should().Be(ApprovalStageStatus.Skipped);
        await DeactivateField(r.Memo);

        var res = await Approve(id, "buyer2");

        res.IsSuccess.Should().BeTrue(Dump(res));
        Body(res).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        Open(PendingOf(po)!.Id, "fin1").Should().NotBeNull();
    }

    // ───────── 重來 ─────────

    [Fact]
    public async Task Reroute_GraphInstance_ClosesActiveAndWaiting_InstanceEnded()
    {
        // 全部到齊的並行中，一條分支已到匯合（waiting）、另一條還在簽；改走別的流程時，舊實例兩種執行線都要關
        var r = await RigWith(async x =>
        {
            await SeedGraphFlow(x, "po-g", x.Confirmed, 1, ForkJoin(ApprovalModes.All) with { When = Cond(x.Total, "lt", 100000) });
            await SeedFlow(x, "po-s", x.Confirmed, 2, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        });
        var (po, id) = await SubmitAs(r, price: 50000);
        await MustApprove(id, "fin1");
        NodeRuns(id, "j").Select(x => x.Status).Should().Equal("waiting");
        await AddExtra(r, po, 120000);

        var res = await Approve(id, "buyer2");

        Body(res).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Rerouted);
        ApprovalGraphPath.Live(RunsOf(id)).Should().BeEmpty();
        RunOf(id, "b").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.InstanceEnded);
        RunOf(id, "j").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.InstanceEnded);
        RunOf(id, "a").Should().Match<ApprovalStageRun>(x => x.Status == "approved" && x.CloseReason == null);
    }

    [Fact]
    public async Task FallbackInstanceOfGraphFlow_UsesSequencePath()
    {
        // 重新比對沒有流程符合 → 備援實例沿用舊的綁定版本（graph），仍走第一版的單一關；同意後核准（J2）
        var r = await Rig(r0 => G([Start(), Approval("a", Users("fin1")), End()], [E("e0", "start", "a"), E("e1", "a", "end")],
            Cond(r0.Total, "gte", 100000)));
        var (po, id) = await SubmitAs(r, price: 150000);
        await AddExtra(r, po, -145000);
        Body(await Approve(id, "fin1")).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        var fb = PendingOf(po)!;
        fb.RouteKind.Should().Be(ApprovalRouteKind.Fallback);
        RunsOf(fb.Id).Should().ContainSingle().Which.NodeId.Should().BeNull();

        var done = await Approve(fb.Id, "admin");

        done.IsSuccess.Should().BeTrue(Dump(done));
        Body(done).Outcome.Should().Be(ApprovalOutcomes.Approved);
        Inst(fb.Id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        StateOf(r, po).Should().Be(r.Confirmed);
    }

    [Fact]
    public async Task Recheck_PicksRequiredInNewGraphFlow_NeedsResubmit()
    {
        // 改走含發起人自選節點的 graph 流程：不建新實例，請發起人重新送出（J25；Picks 要讀 graph 的簽核節點）
        var r = await RigWith(async x =>
        {
            await SeedGraphFlow(x, "po-g", x.Confirmed, 1, G(
                [Start(), Approval("p", new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [FinanceGroup], Min: 1, Max: 1)), End()],
                [E("e0", "start", "p"), E("pe", "p", "end")], Cond(x.Total, "gte", 100000)));
            await SeedFlow(x, "po-s", x.Confirmed, 2, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        });
        var (po, id) = await SubmitAs(r, price: 50000);
        await AddExtra(r, po, 120000);

        var res = await Approve(id, "buyer2");

        res.IsSuccess.Should().BeTrue(Dump(res));
        Body(res).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        Body(res).NeedsResubmit.Should().BeTrue();
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Rerouted);
        PendingOf(po).Should().BeNull();
    }

    [Fact]
    public async Task Recheck_SameGraphFlowRouteChanged_PickNodeOffPath_NeedsResubmit()
    {
        // 同流程同版本、圖內路線改變，且流程含發起人自選節點：不直接建新實例，要發起人重新送出（NeedsPick 取新流程的自選判斷）
        var r = await Rig(r0 => G(
            [Start(), Condition("c"), Approval("p", new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [FinanceGroup], Min: 1, Max: 1)),
             Approval("b", Users("buyer2")), End()],
            [E("e0", "start", "c"), E("cp", "c", "p", Cond(r0.Total, "gte", 100000), order: 1), E("cb", "c", "b", isDefault: true),
             E("pe", "p", "end"), E("be", "b", "end")]));
        As("buyer1");
        var po0 = await NewPo(r, price: 50000);
        var sub = await Submit(po0.Id, r.Confirmed, new() { ["p"] = ["fin1"] });
        sub.IsCreated.Should().BeTrue(Dump(sub));
        var id = ((SubmitApprovalResponse)sub.Data!).InstanceId!.Value;
        RunOf(id, "b").Status.Should().Be(ApprovalStageStatus.Active);
        await AddExtra(r, po0.Id, 120000);

        var res = await Approve(id, "buyer2");

        res.IsSuccess.Should().BeTrue(Dump(res));
        Body(res).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        Body(res).NeedsResubmit.Should().BeTrue();
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Rerouted);
        PendingOf(po0.Id).Should().BeNull();
    }

    [Fact]
    public async Task Approve_RecheckSelectsGraphFlow_ReroutesToGraphInstance()
    {
        // 原擋門測試 Approve_RecheckSelectsGraphFlow_409_Rollback 搬家：sequence 實例的快照裡出現優先序更前的 graph 流程，
        // 同意後的重新比對選到它，改成重來並建出 graph 實例（不再 409）
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 150000);
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        var graphFlow = await SeedGraphFlow(r, "po-g", r.Confirmed, 99, Linear2());   // 真的流程列，重來建出的實例才有外鍵可指
        var inst = Db.Context.Set<ApprovalInstance>().Single(i => i.Id == id);
        inst.FlowSetSnapshot = [new FlowSnapshotEntry(graphFlow.Id, graphFlow.CurrentVersionId!.Value, 0, true, Text("po-g"), Linear2()), .. inst.FlowSetSnapshot];
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        var task = TasksOf(id).Single(t => t.Status == ApprovalTaskStatus.Open);
        As(task.AssigneeUserId);

        var res = await Send(new ApproveTaskRequest(task.Id, Inst(id).RowVersion, null, null));

        res.IsSuccess.Should().BeTrue(Dump(res));
        Body(res).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Rerouted);
        TasksOf(id).Should().NotContain(t => t.Status == ApprovalTaskStatus.Approved);   // 這次同意不計入
        var fresh = PendingOf(po.Id)!;
        fresh.RestartedFromInstanceId.Should().Be(id);
        RunsOf(fresh.Id).Select(x => (x.NodeId, x.Status)).Should().Equal(("start", "passed"), ("a", "active"));
        Open(fresh.Id, "fin1").Should().NotBeNull();
    }

    // ───────── 結構再驗、可編輯欄位、加簽 ─────────

    [Fact]
    public async Task Approve_BrokenBoundGraph_409_Alert_NothingWritten()
    {
        // 綁定定義缺預設線（繞過驗證寫入）：即使快照裡另有一條重新比對會選到的 sequence 流程，同意也先 409＋異常通知，不重來（J7）
        var r = await Rig(Linear2());
        var (_, id) = await SubmitAs(r);
        var inst = Db.Context.Set<ApprovalInstance>().Single(i => i.Id == id);
        var good = inst.FlowSetSnapshot.Single();
        var broken = G([Start(), Condition("c"), Approval("a", Users("fin1")), End()],
            [E("e0", "start", "c"), E("ca", "c", "a", Cond(r.Total, "gte", 1), order: 1), E("cb", "c", "end", Cond(r.Total, "gte", 2), order: 2), E("ae", "a", "end")]);
        inst.FlowSetSnapshot = [new FlowSnapshotEntry(9998, 9998, 0, true, Text("po-x"), Def(null, Stage("lead", Group(PurchaseLeadGroup)))), good with { Definition = broken }];
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        var before = (Inst(id).RowVersion, ActionsOf(id).Count);

        var res = await Approve(id, "fin1");

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.GraphRuntimeInvalid.Resolve(null));
        (Inst(id).RowVersion, ActionsOf(id).Count).Should().Be(before);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        Open(id, "fin1").Status.Should().Be(ApprovalTaskStatus.Open);
        ActionsOf(id).Should().NotContain(a => a.Action == ApprovalActions.Reroute);
        Notices("admin", NotificationKinds.ApprovalAdminAlert).Should().Be(1);
    }

    [Fact]
    public async Task Approve_GraphNodeEditableFields_AllowedOnlyThoseOfThisNode()
    {
        var r = await Rig(r0 => G([Start(), Approval("a", Users("fin1"), editable: [r0.DueDate]), End()], [E("e0", "start", "a"), E("e1", "a", "end")]));
        var (po, id) = await SubmitAs(r);

        var outside = await Approve(id, "fin1", V((r.Memo, "x")));
        outside.Errors.Should().ContainKey($"f_{r.Memo}");
        Open(id, "fin1").Status.Should().Be(ApprovalTaskStatus.Open);

        var ok = await Approve(id, "fin1", V((r.DueDate, "2026-12-01")));

        ok.IsSuccess.Should().BeTrue(Dump(ok));
        Rec(po).Values[$"f_{r.DueDate}"]!.Value.GetString().Should().Be("2026-12-01");
    }
}
