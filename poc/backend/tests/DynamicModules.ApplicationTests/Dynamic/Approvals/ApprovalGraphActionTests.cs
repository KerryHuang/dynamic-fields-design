using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 流程圖的退回、撤回、終止（計畫 fg2 Task 6；流程圖設計 §8.4–§8.6；U11 結構不合法時的動作對照）。
/// 簽核人：a、a1＝fin1、a2＝fin2、b＝buyer2、c＝gm；發起人 buyer1。
/// </summary>
public class ApprovalGraphActionTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    // ───────── 圖與情境 ─────────

    /// <summary>start → a → b → end。</summary>
    private static ApprovalDefinition Linear2() => G(
        [Start(), Approval("a", Users("fin1")), Approval("b", Users("buyer2")), End()],
        [E("e0", "start", "a"), E("ab", "a", "b"), E("be", "b", "end")]);

    /// <summary>start → fork f → 分支 a、b → join j → c → end；a 是 aSource（預設 fin1），aMode 是它的會簽或簽。</summary>
    private static ApprovalDefinition ForkJoin(string joinMode, ApproverSource? aSource = null, string aMode = ApprovalModes.Any) => G(
        [Start(), Fork("f"), Approval("a", aSource ?? Users("fin1"), mode: aMode), Approval("b", Users("buyer2")), Join("j", "f", joinMode),
         Approval("c", Users("gm")), End()],
        [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("jc", "j", "c"), E("ce", "c", "end")]);

    /// <summary>分支 fa 內連續兩個簽核 a1（fin1）→ a2（fin2），分支 fb 是 b（buyer2）；全部到齊後 c（gm）。</summary>
    private static ApprovalDefinition BranchSerial() => G(
        [Start(), Fork("f"), Approval("a1", Users("fin1")), Approval("a2", Users("fin2")), Approval("b", Users("buyer2")), Join("j", "f"),
         Approval("c", Users("gm")), End()],
        [E("e0", "start", "f"), E("fa", "f", "a1"), E("fb", "f", "b"), E("a12", "a1", "a2"), E("a2j", "a2", "j"), E("bj", "b", "j"),
         E("jc", "j", "c"), E("ce", "c", "end")]);

    /// <summary>外層 f（全部到齊 j）：分支 fx 是 x（fin1），分支 ff2 是內層 f2（全部到齊 j2）下的 y（buyer2）、z（emp1），j2 之後接 w（fin2）。</summary>
    private static ApprovalDefinition NestedThenNode() => G(
        [Start(), Fork("f"), Approval("x", Users("fin1")), Fork("f2"), Approval("y", Users("buyer2")), Approval("z", Users("emp1")),
         Join("j2", "f2"), Approval("w", Users("fin2")), Join("j", "f"), End()],
        [E("e0", "start", "f"), E("fx", "f", "x"), E("ff2", "f", "f2"), E("f2y", "f2", "y"), E("f2z", "f2", "z"), E("yj2", "y", "j2"),
         E("zj2", "z", "j2"), E("j2w", "j2", "w"), E("wj", "w", "j"), E("xj", "x", "j"), E("je", "j", "end")]);

    /// <summary>start → a（fin1）→ 條件節點 c（總額 ≥ 100000 走 x emp1，預設走 y buyer2）→ m（fin2）→ end。</summary>
    private static ApprovalDefinition CondAfterA(PurchaseRig r) => G(
        [Start(), Approval("a", Users("fin1")), Condition("c"), Approval("x", Users("emp1")), Approval("y", Users("buyer2")), Approval("m", Users("fin2")), End()],
        [E("e0", "start", "a"), E("ac", "a", "c"), E("cx", "c", "x", Cond(r.Total, "gte", 100000), order: 1), E("cy", "c", "y", isDefault: true),
         E("xm", "x", "m"), E("ym", "y", "m"), E("me", "m", "end")]);

    private async Task<PurchaseRig> RigWith(Func<PurchaseRig, Task> seed, Func<PurchaseRig, WorkflowTransition>? extraTransition = null)
    {
        var r = await BuildPurchase();
        await seed(r);
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled), .. extraTransition is null ? [] : new[] { extraTransition(r) }])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
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

    private List<ApprovalStageRun> RunsOf(long id) =>
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == id).OrderBy(x => x.Seq).ToList();

    private List<ApprovalStageRun> NodeRuns(long id, string nodeId) => RunsOf(id).Where(x => x.NodeId == nodeId).ToList();

    private ApprovalStageRun RunOf(long id, string nodeId) => NodeRuns(id, nodeId).Single();

    private ApprovalTask Open(long id, string user) => TasksOf(id).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private Task<ServiceResult> Approve(long id, string user)
    {
        As(user);
        return Send(new ApproveTaskRequest(Open(id, user).Id, Inst(id).RowVersion, null, null));
    }

    private async Task MustApprove(long id, string user)
    {
        var res = await Approve(id, user);
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private Task<ServiceResult> Reject(long id, string user, string to)
    {
        As(user);
        return Send(new RejectTaskRequest(Open(id, user).Id, Inst(id).RowVersion, "請重看", to));
    }

    private Task<ServiceResult> Withdraw(long id, string user = "buyer1")
    {
        As(user);
        return Send(new WithdrawApprovalRequest(id, Inst(id).RowVersion, null));
    }

    private Task<ServiceResult> Cancel(long id, string user = "admin")
    {
        As(user);
        return Send(new CancelApprovalRequest(id, Inst(id).RowVersion, "供應商報價已過期，請重新詢價後再送"));
    }

    private static string Outcome(ServiceResult r) => ((ApprovalActionResponse)r.Data!).Outcome;

    private async Task AddRun(ApprovalStageRun run)
    {
        Db.Context.Add(run);
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    /// <summary>把實例綁定的定義換成缺預設線的壞圖（繞過驗證寫入）；節點實例與任務不動。</summary>
    private async Task BreakDefinition(PurchaseRig r, long id)
    {
        var inst = Db.Context.Set<ApprovalInstance>().Single(i => i.Id == id);
        var broken = G([Start(), Condition("c"), Approval("a", Users("fin1")), End()],
            [E("e0", "start", "c"), E("ca", "c", "a", Cond(r.Total, "gte", 1), order: 1), E("cb", "c", "end", Cond(r.Total, "gte", 2), order: 2), E("ae", "a", "end")]);
        inst.FlowSetSnapshot = [inst.FlowSetSnapshot.Single() with { Definition = broken }];
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    // ───────── 退回發起人 ─────────

    [Fact]
    public async Task RejectToInitiator_InBranch_AllLinesClosed_InstanceEnded()
    {
        var r = await Rig(ForkJoin(ApprovalModes.All));
        var (po, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");   // a 通過、匯合等待中；b 還在簽

        var res = await Reject(id, "buyer2", RejectTaskHandler.ToInitiator);

        res.IsSuccess.Should().BeTrue(Dump(res));
        Outcome(res).Should().Be(ApprovalOutcomes.Rejected);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Rejected);
        Inst(id).CurrentStageRunId.Should().BeNull();
        ApprovalGraphPath.Live(RunsOf(id)).Should().BeEmpty();
        RunOf(id, "b").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.InstanceEnded);
        RunOf(id, "j").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.InstanceEnded);
        RunOf(id, "a").Should().Match<ApprovalStageRun>(x => x.Status == "approved" && x.CloseReason == null);
        TasksOf(id).Should().NotContain(t => t.Status == ApprovalTaskStatus.Open);
        StateOf(r, po).Should().Be(r.Draft);
    }

    [Fact]
    public async Task RejectToInitiator_OtherBranchActive_ClosedAndItsTaskClosed()
    {
        var r = await Rig(ForkJoin(ApprovalModes.All));
        var (_, id) = await SubmitAs(r);

        (await Reject(id, "buyer2", RejectTaskHandler.ToInitiator)).IsSuccess.Should().BeTrue();

        RunOf(id, "a").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.InstanceEnded);
        TasksOf(id).Single(t => t.AssigneeUserId == "fin1").Status.Should().Be(ApprovalTaskStatus.Closed);
        TasksOf(id).Single(t => t.AssigneeUserId == "buyer2").Status.Should().Be(ApprovalTaskStatus.Rejected);
    }

    [Fact]
    public async Task RejectToInitiator_RejectToStateRuns()
    {
        var r = await RigWith(x => SeedGraphFlow(x, "po-g", x.Confirmed, 1, ForkJoin(ApprovalModes.All) with { RejectTo = x.Returned }),
            x => Plain(x.Draft, x.Returned));
        var (po, id) = await SubmitAs(r);

        (await Reject(id, "fin1", RejectTaskHandler.ToInitiator)).IsSuccess.Should().BeTrue();

        StateOf(r, po).Should().Be(r.Returned);
        Notices("buyer1", NotificationKinds.ApprovalRejected).Should().Be(1);
    }

    // ───────── 退回上一關 ─────────

    [Fact]
    public async Task ReturnPrevious_InBranch_RestartsSameBranchNode()
    {
        var r = await Rig(BranchSerial());
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");   // a1 通過 → a2（fin2）

        var res = await Reject(id, "fin2", RejectTaskHandler.ToPrevious);

        res.IsSuccess.Should().BeTrue(Dump(res));
        Outcome(res).Should().Be(ApprovalOutcomes.Returned);
        RunOf(id, "a2").Status.Should().Be(ApprovalStageStatus.Returned);
        var restarted = NodeRuns(id, "a1").Last();
        restarted.Should().Match<ApprovalStageRun>(x => x.Status == "active" && x.StageCode == "a1" && x.NodeKind == "approval" && x.BranchKey == "fa" && x.InEdgeId == "fa");
        restarted.Seq.Should().Be(RunsOf(id).Max(x => x.Seq));
        Open(id, "fin1").StageRunId.Should().Be(restarted.Id);
        Notices("fin1", NotificationKinds.ApprovalReturned).Should().Be(1);
        Notices("buyer1", NotificationKinds.ApprovalReturned).Should().Be(1);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        Inst(id).CurrentStageRunId.Should().Be(RunOf(id, "b").Id);   // Seq 最小的 active：另一條分支的 b，不是剛重啟的 a1
    }

    [Fact]
    public async Task ReturnPrevious_InBranch_OtherBranchTaskStaysOpen()
    {
        var r = await Rig(BranchSerial());
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");

        (await Reject(id, "fin2", RejectTaskHandler.ToPrevious)).IsSuccess.Should().BeTrue();

        RunOf(id, "b").Status.Should().Be(ApprovalStageStatus.Active);
        Open(id, "buyer2").Status.Should().Be(ApprovalTaskStatus.Open);
        TasksOf(id).Single(t => t.AssigneeUserId == "fin2").Status.Should().Be(ApprovalTaskStatus.Rejected);
    }

    [Fact]
    public async Task ReturnPrevious_FirstInBranch_400()
    {
        var r = await Rig(BranchSerial());
        var (_, id) = await SubmitAs(r);
        var before = (Inst(id).RowVersion, ActionsOf(id).Count, RunsOf(id).Count);

        var res = await Reject(id, "fin1", RejectTaskHandler.ToPrevious);

        res.Errors["to"].Should().Equal(ApprovalMessages.NoPreviousStage.Resolve(null));
        (Inst(id).RowVersion, ActionsOf(id).Count, RunsOf(id).Count).Should().Be(before);
        Open(id, "fin1").Status.Should().Be(ApprovalTaskStatus.Open);
    }

    [Fact]
    public async Task ReturnPrevious_FirstAfterJoin_400()
    {
        var r = await Rig(ForkJoin(ApprovalModes.All));
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");
        await MustApprove(id, "buyer2");

        var res = await Reject(id, "gm", RejectTaskHandler.ToPrevious);

        res.Errors["to"].Should().Equal(ApprovalMessages.NoPreviousStage.Resolve(null));
        Open(id, "gm").Status.Should().Be(ApprovalTaskStatus.Open);
    }

    [Fact]
    public async Task ReturnPrevious_AfterInnerJoinInOuterBranch_400()
    {
        // 外層分支內、內層匯合之後的第一個簽核節點：內層匯合列的 BranchKey 是外層分支段，上一關以它為邊界，所以沒有上一關
        var r = await Rig(NestedThenNode());
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "buyer2");
        await MustApprove(id, "emp1");
        RunOf(id, "w").Status.Should().Be(ApprovalStageStatus.Active);

        var res = await Reject(id, "fin2", RejectTaskHandler.ToPrevious);

        res.Errors["to"].Should().Equal(ApprovalMessages.NoPreviousStage.Resolve(null));
    }

    [Fact]
    public async Task ReturnPrevious_RootSegment_SameAsSequence()
    {
        var r = await Rig(Linear2());
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");

        var res = await Reject(id, "buyer2", RejectTaskHandler.ToPrevious);

        Outcome(res).Should().Be(ApprovalOutcomes.Returned);
        RunsOf(id).Select(x => (x.NodeId, x.Status)).Should().Equal(("start", "passed"), ("a", "approved"), ("b", "returned"), ("a", "active"));
        Open(id, "fin1").StageRunId.Should().Be(RunsOf(id).Last().Id);
        Inst(id).CurrentStageRunId.Should().Be(RunsOf(id).Last().Id);
        Notices("fin1", NotificationKinds.ApprovalReturned).Should().Be(1);
    }

    [Fact]
    public async Task ReturnPrevious_ThenApprove_ConditionReevaluated()
    {
        // a → 條件 c → y：y 退回上一關重啟 a；期間總額變大，a 再同意後 c 用當下的值重跑，改走 x
        var r = await Rig(CondAfterA);
        var (po, id) = await SubmitAs(r, price: 5000);
        await MustApprove(id, "fin1");
        RunOf(id, "y").Status.Should().Be(ApprovalStageStatus.Active);
        (await Reject(id, "buyer2", RejectTaskHandler.ToPrevious)).IsSuccess.Should().BeTrue();
        As("buyer1");
        (await Send(new DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord.CreateRecordRequest(r.Extra,
            V((TitleId(r.Extra), "追加"), (r.ExtraRef, po), (r.ExtraAmount, 120000m))))).IsCreated.Should().BeTrue();

        var res = await Approve(id, "fin1");

        res.IsSuccess.Should().BeTrue(Dump(res));
        Outcome(res).Should().Be(ApprovalOutcomes.Advanced);
        NodeRuns(id, "c").Should().HaveCount(2);
        NodeRuns(id, "x").Should().ContainSingle().Which.Status.Should().Be(ApprovalStageStatus.Active);
        Open(id, "emp1").Should().NotBeNull();
        NodeRuns(id, "y").Select(x => x.Status).Should().Equal("returned");
    }

    [Fact]
    public async Task Restart_NormalNode_CarriesGraphFields()
    {
        var r = await Rig(BranchSerial());
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");
        var original = RunOf(id, "a1");

        await Reject(id, "fin2", RejectTaskHandler.ToPrevious);

        var again = NodeRuns(id, "a1").Last();
        again.Id.Should().NotBe(original.Id);
        (again.StageCode, again.NodeId, again.NodeKind, again.BranchKey, again.InEdgeId, again.Mode)
            .Should().Be((original.StageCode, original.NodeId, original.NodeKind, original.BranchKey, original.InEdgeId, original.Mode));
        again.NameSnapshot!.Resolve("zh-TW").Should().Be("a1");
    }

    [Fact]
    public async Task Restart_AddSignTarget_CarriesGraphFields()
    {
        // b 退回上一關，上一關是 a 的已同意加簽（手寫節點實例：graph 的加簽寫入在 fg2d）：重啟的加簽列要帶齊 graph 欄位
        var r = await Rig(Linear2());
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");
        await Db.Context.Set<ApprovalStageRun>().Where(x => x.InstanceId == id && x.NodeId == "b").ExecuteUpdateAsync(s => s.SetProperty(x => x.Seq, 4));
        await AddRun(new ApprovalStageRun
        {
            InstanceId = id, Seq = 3, StageCode = null, NodeId = "a", NodeKind = "approval", BranchKey = "", InEdgeId = null,
            Status = ApprovalStageStatus.Approved, Mode = ApprovalModes.Any, AddedByUserId = "fin1", AddSignUserIds = ["emp1"]
        });

        var res = await Reject(id, "buyer2", RejectTaskHandler.ToPrevious);

        res.IsSuccess.Should().BeTrue(Dump(res));
        var again = RunsOf(id).Last();
        again.Should().Match<ApprovalStageRun>(x => x.Status == "active" && x.StageCode == null && x.NodeId == "a" && x.NodeKind == "approval"
            && x.BranchKey == "" && x.InEdgeId == null && x.AddedByUserId == "fin1");
        again.AddSignUserIds.Should().Equal("emp1");
        Open(id, "emp1").StageRunId.Should().Be(again.Id);
        RunOf(id, "b").Status.Should().Be(ApprovalStageStatus.Returned);
    }

    [Fact]
    public async Task Restart_RequeuedAddSign_CarriesGraphFields()
    {
        // 被退回的是 a 的加簽（手寫）：退回目標 a 重啟，被退回的加簽排回隊伍，排隊列也要帶齊 graph 欄位
        var r = await Rig(Linear2());
        var (_, id) = await SubmitAs(r);
        var a = RunOf(id, "a");
        await Db.Context.Set<ApprovalStageRun>().Where(x => x.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ApprovalStageStatus.Approved));
        await Db.Context.Set<ApprovalTask>().Where(x => x.StageRunId == a.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ApprovalTaskStatus.Approved));
        var sign = new ApprovalStageRun
        {
            InstanceId = id, Seq = 3, StageCode = null, NodeId = "a", NodeKind = "approval", BranchKey = "", InEdgeId = null,
            Status = ApprovalStageStatus.Active, Mode = ApprovalModes.Any, AddedByUserId = "fin1", AddSignUserIds = ["emp1"]
        };
        await AddRun(sign);
        Db.Context.Add(new ApprovalTask
        {
            StageRunId = sign.Id, InstanceId = id, RecordId = Inst(id).RecordId, AssigneeUserId = "emp1", OriginalUserId = "emp1", SeatKey = "emp1",
            Reason = ApprovalTaskReason.Added, Status = ApprovalTaskStatus.Open, CreatedTime = SeedTime
        });
        await Db.Context.SaveChangesAsync();
        await Db.Context.Set<ApprovalInstance>().Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CurrentStageRunId, sign.Id));
        Db.Context.ChangeTracker.Clear();

        var res = await Reject(id, "emp1", RejectTaskHandler.ToPrevious);

        res.IsSuccess.Should().BeTrue(Dump(res));
        var runs = RunsOf(id);
        runs.Single(x => x.Id == sign.Id).Status.Should().Be(ApprovalStageStatus.Returned);
        runs.Where(x => x.NodeId == "a").Last(x => x.StageCode == "a").Status.Should().Be(ApprovalStageStatus.Active);
        var queued = runs.Single(x => x.Status == ApprovalStageStatus.Queued);
        queued.Should().Match<ApprovalStageRun>(x => x.StageCode == null && x.NodeId == "a" && x.NodeKind == "approval" && x.BranchKey == ""
            && x.InEdgeId == null && x.AddedByUserId == "fin1");
        queued.AddSignUserIds.Should().Equal("emp1");
        queued.Seq.Should().Be(runs.Max(x => x.Seq));
    }

    // ───────── 撤回 ─────────

    [Fact]
    public async Task Withdraw_NoActionOnActives_Ok_AllLinesClosed()
    {
        var r = await Rig(ForkJoin(ApprovalModes.All));
        var (po, id) = await SubmitAs(r);

        var res = await Withdraw(id);

        res.IsSuccess.Should().BeTrue(Dump(res));
        Outcome(res).Should().Be(ApprovalOutcomes.Withdrawn);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Withdrawn);
        Inst(id).CurrentStageRunId.Should().BeNull();
        ApprovalGraphPath.Live(RunsOf(id)).Should().BeEmpty();
        RunsOf(id).Where(x => x.NodeId is "a" or "b").Should().OnlyContain(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.InstanceEnded);
        TasksOf(id).Should().OnlyContain(t => t.Status == ApprovalTaskStatus.Closed);
        Notices("fin1", NotificationKinds.ApprovalWithdrawn).Should().Be(1);
        Notices("buyer2", NotificationKinds.ApprovalWithdrawn).Should().Be(1);
        StateOf(r, po).Should().Be(r.Draft);
        ActionsOf(id).Single(a => a.Action == ApprovalActions.Withdraw).StageRunId.Should().NotBeNull();
    }

    [Fact]
    public async Task Withdraw_PreviousNodeInBranchApproved_StillOk()
    {
        // 分支內前一個節點已核准、目前節點沒有人動作：可撤回（§8.5 看每個 active 的簽核節點）
        var r = await Rig(BranchSerial());
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");

        var res = await Withdraw(id);

        res.IsSuccess.Should().BeTrue(Dump(res));
        RunOf(id, "a2").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.InstanceEnded);
        RunOf(id, "a1").Status.Should().Be(ApprovalStageStatus.Approved);
    }

    [Fact]
    public async Task Withdraw_OneBranchWaitingAtJoin_Ok()
    {
        var r = await Rig(ForkJoin(ApprovalModes.All));
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");   // 一條分支到匯合等待；a 的任務是 approved，但 a 已不是 active

        var res = await Withdraw(id);

        res.IsSuccess.Should().BeTrue(Dump(res));
        RunOf(id, "j").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.InstanceEnded);
        ApprovalGraphPath.Live(RunsOf(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Withdraw_AnyActiveHasApproval_409()
    {
        // 並行中某個 active 節點已有人同意（會簽席位）：不能撤回；另一條分支沒動作也一樣
        var r = await Rig(ForkJoin(ApprovalModes.All, Group(FinanceGroup), ApprovalModes.All));
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");
        var before = (Inst(id).RowVersion, ActionsOf(id).Count);

        var res = await Withdraw(id);

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.CannotWithdraw.Resolve(null));
        (Inst(id).RowVersion, ActionsOf(id).Count).Should().Be(before);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
    }

    [Fact]
    public async Task Withdraw_CancelledBranchIgnored()
    {
        // 任一到齊通過後，被取消的分支（曾有席位同意）已不是 active，不擋撤回
        var r = await Rig(ForkJoin(ApprovalModes.Any, Group(FinanceGroup), ApprovalModes.All));
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");
        await MustApprove(id, "buyer2");
        RunOf(id, "a").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.BranchCancelled);
        RunOf(id, "c").Status.Should().Be(ApprovalStageStatus.Active);

        var res = await Withdraw(id);

        res.IsSuccess.Should().BeTrue(Dump(res));
        RunOf(id, "c").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.InstanceEnded);
        RunOf(id, "a").CloseReason.Should().Be(ApprovalCloseReasons.BranchCancelled);   // 被取消的原因不被覆寫
    }

    // ───────── 終止 ─────────

    [Fact]
    public async Task Cancel_GraphInstance_AllLinesClosed_InstanceEnded()
    {
        var r = await Rig(ForkJoin(ApprovalModes.All));
        var (po, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");   // 匯合等待中、b 還在簽

        var res = await Cancel(id);

        res.IsSuccess.Should().BeTrue(Dump(res));
        Outcome(res).Should().Be(ApprovalOutcomes.Cancelled);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Cancelled);
        Inst(id).CurrentStageRunId.Should().BeNull();
        ApprovalGraphPath.Live(RunsOf(id)).Should().BeEmpty();
        RunOf(id, "b").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.InstanceEnded);
        RunOf(id, "j").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == ApprovalCloseReasons.InstanceEnded);
        TasksOf(id).Should().NotContain(t => t.Status == ApprovalTaskStatus.Open);
        StateOf(r, po).Should().Be(r.Draft);
    }

    // ───────── 結構不合法時的動作對照（U11）─────────

    [Fact]
    public async Task BrokenGraph_ReturnPrevious_409_Alert()
    {
        var r = await Rig(BranchSerial());
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");
        await BreakDefinition(r, id);
        var before = (Inst(id).RowVersion, ActionsOf(id).Count, RunsOf(id).Count);

        var res = await Reject(id, "fin2", RejectTaskHandler.ToPrevious);

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.GraphRuntimeInvalid.Resolve(null));
        (Inst(id).RowVersion, ActionsOf(id).Count, RunsOf(id).Count).Should().Be(before);
        Open(id, "fin2").Status.Should().Be(ApprovalTaskStatus.Open);
        Notices("admin", NotificationKinds.ApprovalAdminAlert).Should().Be(1);
    }

    [Fact]
    public async Task BrokenGraph_Withdraw_Ok()
    {
        var r = await Rig(ForkJoin(ApprovalModes.All));
        var (po, id) = await SubmitAs(r);
        await BreakDefinition(r, id);

        var res = await Withdraw(id);

        res.IsSuccess.Should().BeTrue(Dump(res));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Withdrawn);
        ApprovalGraphPath.Live(RunsOf(id)).Should().BeEmpty();
        StateOf(r, po).Should().Be(r.Draft);
        Notices("admin", NotificationKinds.ApprovalAdminAlert).Should().Be(0);
    }

    [Fact]
    public async Task BrokenGraph_Cancel_Ok()
    {
        var r = await Rig(ForkJoin(ApprovalModes.All));
        var (_, id) = await SubmitAs(r);
        await BreakDefinition(r, id);

        var res = await Cancel(id);

        res.IsSuccess.Should().BeTrue(Dump(res));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Cancelled);
        ApprovalGraphPath.Live(RunsOf(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task BrokenGraph_RejectToInitiator_Ok()
    {
        var r = await Rig(ForkJoin(ApprovalModes.All));
        var (_, id) = await SubmitAs(r);
        await BreakDefinition(r, id);

        var res = await Reject(id, "buyer2", RejectTaskHandler.ToInitiator);

        res.IsSuccess.Should().BeTrue(Dump(res));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Rejected);
        ApprovalGraphPath.Live(RunsOf(id)).Should().BeEmpty();
        Notices("admin", NotificationKinds.ApprovalAdminAlert).Should().Be(0);
    }

    // ───────── fg2c 審查補測（M1、S1–S3）─────────

    [Fact]
    public async Task Withdraw_PartialApprovalInLaterBranch_409()
    {
        // 分支 fa：a（buyer2）沒人動作、Seq 較小所以是 CurrentStageRunId；分支 fb：b 是財務會簽，fin2 已同意一席 → 不能撤回
        var g = G([Start(), Fork("f"), Approval("a", Users("buyer2")), Approval("b", Group(FinanceGroup), mode: ApprovalModes.All), Join("j", "f"),
            End()], [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("je", "j", "end")]);
        var r = await Rig(g);
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "fin2");
        RunOf(id, "b").Status.Should().Be(ApprovalStageStatus.Active);
        Inst(id).CurrentStageRunId.Should().Be(RunOf(id, "a").Id);

        var res = await Withdraw(id);

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.CannotWithdraw.Resolve(null));
    }

    [Fact]
    public async Task ReturnPrevious_OtherBranchQueuedAddSign_Kept()
    {
        var r = await Rig(BranchSerial());
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");
        var b = RunOf(id, "b");
        await AddRun(new ApprovalStageRun
        {
            InstanceId = id, Seq = RunsOf(id).Max(x => x.Seq) + 1, StageCode = null, NodeId = "b", NodeKind = "approval", BranchKey = b.BranchKey,
            Status = ApprovalStageStatus.Queued, Mode = ApprovalModes.Any, AddedByUserId = "buyer2", AddSignUserIds = ["emp1"]
        });

        (await Reject(id, "fin2", RejectTaskHandler.ToPrevious)).IsSuccess.Should().BeTrue();

        RunsOf(id).Single(x => x.StageCode == null && x.NodeId == "b").Status.Should().Be(ApprovalStageStatus.Queued);
    }

    [Fact]
    public async Task AddSign_BrokenGraph_409_GraphRuntimeInvalid_Alert()
    {
        var r = await Rig(Linear2());
        var (_, id) = await SubmitAs(r);
        var task = Open(id, "fin1");
        await BreakDefinition(r, id);
        As("fin1");

        var res = await Send(new AddSignTaskRequest(task.Id, Inst(id).RowVersion, null, null, ["emp1"], ApprovalModes.All));

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.GraphRuntimeInvalid.Resolve(null));
        Notices("admin", NotificationKinds.ApprovalAdminAlert).Should().Be(1);
    }

    [Fact]
    public async Task ReturnPrevious_ToAddSign_CurrentRecomputedToMinSeqActive()
    {
        // 分支 fa：a1 已同意、a1 的加簽（手寫）已同意、a2 進行中；a2 退回上一關 → 重啟加簽；目前關卡要重算成另一條分支 Seq 較小的 b
        var r = await Rig(BranchSerial());
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");
        var a2 = RunOf(id, "a2");
        await Db.Context.Set<ApprovalStageRun>().Where(x => x.Id == a2.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Seq, a2.Seq + 1));
        await AddRun(new ApprovalStageRun
        {
            InstanceId = id, Seq = a2.Seq, StageCode = null, NodeId = "a1", NodeKind = "approval", BranchKey = "fa", InEdgeId = null,
            Status = ApprovalStageStatus.Approved, Mode = ApprovalModes.Any, AddedByUserId = "fin1", AddSignUserIds = ["emp1"]
        });

        var res = await Reject(id, "fin2", RejectTaskHandler.ToPrevious);

        res.IsSuccess.Should().BeTrue(Dump(res));
        RunsOf(id).Last().Should().Match<ApprovalStageRun>(x => x.StageCode == null && x.Status == "active");
        Inst(id).CurrentStageRunId.Should().Be(RunOf(id, "b").Id);
    }
}
