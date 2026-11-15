using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 流程圖引擎的送出與推進（計畫 fg2 Task 4；流程圖設計 §8.1–§8.3、§8.8）：測試主機換成開啟的擋門，graph 才跑得到引擎。
/// 簽核人：a＝fin1、b＝buyer2、c＝gm、z＝emp1；發起人 buyer1。
/// </summary>
public class ApprovalGraphSubmitTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    protected override void ConfigureServices(IServiceCollection services) => OpenGate(services);

    // ───────── 圖與情境 ─────────

    /// <summary>start → a → end。</summary>
    private static ApprovalDefinition Linear() =>
        G([Start(), Approval("a", Users("fin1")), End()], [E("e0", "start", "a"), E("e1", "a", "end")]);

    /// <summary>start → fork f → 分支 a、b → join j → c → end；分支出線依 fa、fb 順序。</summary>
    private static ApprovalDefinition ForkJoin(string joinMode) => G(
        [Start(), Fork("f"), Approval("a", Users("fin1")), Approval("b", Users("buyer2")), Join("j", "f", joinMode), Approval("c", Users("gm")), End()],
        [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("jc", "j", "c"), E("ce", "c", "end")]);

    /// <summary>fork f 的兩條分支：a（簽核）與空分支（f → j 直連）；emptyFirst 決定出線的定義順序。</summary>
    private static ApprovalDefinition EmptyArm(string joinMode, bool emptyFirst)
    {
        var arms = new List<ApprovalEdgeDef> { E("fa", "f", "a"), E("fe", "f", "j") };
        if (emptyFirst) arms.Reverse();
        return G([Start(), Fork("f"), Approval("a", Users("fin1")), Join("j", "f", joinMode), Approval("c", Users("gm")), End()],
            [E("e0", "start", "f"), .. arms, E("aj", "a", "j"), E("jc", "j", "c"), E("ce", "c", "end")]);
    }

    /// <summary>外層 f（全部到齊 j）：分支 fx 是簽核 x，分支 ff2 是內層 f2（匯合 j2，模式 inner）下的 y、z。</summary>
    private static ApprovalDefinition Nested(string inner) => G(
        [Start(), Fork("f"), Approval("x", Users("fin1")), Fork("f2"), Approval("y", Users("buyer2")), Approval("z", Users("emp1")),
         Join("j2", "f2", inner), Join("j", "f"), End()],
        [E("e0", "start", "f"), E("fx", "f", "x"), E("ff2", "f", "f2"), E("f2y", "f2", "y"), E("f2z", "f2", "z"),
         E("yj2", "y", "j2"), E("zj2", "z", "j2"), E("j2j", "j2", "j"), E("xj", "x", "j"), E("je", "j", "end")]);

    /// <summary>
    /// 條件節點 c：總額 ≥ 10000 走 b（order 1，陣列順序在後）、≥ 1000 走 a（order 2，陣列順序在前）、其餘預設線走 d；三個簽核都接 end。
    /// </summary>
    private static ApprovalDefinition CondByOrder(PurchaseRig r) => G(
        [Start(), Condition("c"), Approval("a", Users("fin1")), Approval("b", Users("buyer2")), Approval("d", Users("gm")), End()],
        [E("e0", "start", "c"), E("ca", "c", "a", Cond(r.Total, "gte", 1000), order: 2), E("cb", "c", "b", Cond(r.Total, "gte", 10000), order: 1),
         E("cd", "c", "d", isDefault: true), E("ae", "a", "end"), E("be", "b", "end"), E("de", "d", "end")]);

    /// <summary>條件節點 c：總額 ≥ 10000 走簽核 a，預設線直接到 end。</summary>
    private static ApprovalDefinition CondToEnd(PurchaseRig r) => G(
        [Start(), Condition("c"), Approval("a", Users("fin1")), End()],
        [E("e0", "start", "c"), E("ca", "c", "a", Cond(r.Total, "gte", 10000), order: 1), E("ce", "c", "end", isDefault: true), E("ae", "a", "end")]);

    /// <summary>分支 1 內有條件節點（≥ 100000 經 a1，預設線直接到 j），分支 2 是簽核 b；全部到齊。</summary>
    private static ApprovalDefinition ConditionIntoJoin(PurchaseRig r) => G(
        [Start(), Fork("f"), Condition("c"), Approval("a1", Users("fin1")), Approval("b", Users("buyer2")), Join("j", "f"), End()],
        [E("e0", "start", "f"), E("fc", "f", "c"), E("fb", "f", "b"), E("ca", "c", "a1", Cond(r.Total, "gte", 100000), order: 1),
         E("cj", "c", "j", isDefault: true), E("a1j", "a1", "j"), E("bj", "b", "j"), E("je", "j", "end")]);

    private async Task<PurchaseRig> Rig(Func<PurchaseRig, ApprovalDefinition> graph)
    {
        var r = await BuildPurchase();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, graph(r));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        return r;
    }

    private Task<PurchaseRig> Rig(ApprovalDefinition graph) => Rig(_ => graph);

    private async Task<(long Po, long Inst, SubmitApprovalResponse Res)> SubmitAs(PurchaseRig r, decimal price = 5000, string user = "buyer1")
    {
        As(user);
        var po = await NewPo(r, price: price);
        var res = await MustSubmit(po.Id, r.Confirmed);
        return (po.Id, res.InstanceId!.Value, res);
    }

    private List<ApprovalStageRun> RunsOf(long id) =>
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == id).OrderBy(x => x.Seq).ToList();

    private static string Name(ApprovalStageRun r) => r.NameSnapshot?.Resolve("zh-TW") ?? "";

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

    private ApprovalDefinition DefinitionOf(long id) => ApprovalEngine.DefinitionOf(Inst(id))!;

    private int Count(long id, string action) => ActionsOf(id).Count(a => a.Action == action);

    private async Task AddRows(params ApprovalStageRun[] rows)
    {
        Db.Context.AddRange(rows);
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    private static JsonElement DataOf(ApprovalAction a) => JsonDocument.Parse(a.DataJson!).RootElement;

    // ───────── 線性、條件 ─────────

    [Fact]
    public async Task Submit_Linear_StartPassed_FirstApprovalActive()
    {
        var r = await Rig(Linear());
        var (_, id, res) = await SubmitAs(r);

        res.Outcome.Should().Be(ApprovalOutcomes.Submitted);
        RunsOf(id).Select(x => (x.NodeId, x.NodeKind, x.Status)).Should().Equal(("start", "start", "passed"), ("a", "approval", "active"));
        TasksOf(id).Select(t => t.AssigneeUserId).Should().Equal("fin1");
        Notices("fin1", NotificationKinds.ApprovalPending).Should().Be(1);
    }

    [Fact]
    public async Task Submit_RunsCarryNodeFields()
    {
        var r = await Rig(Linear());
        var (_, id, _) = await SubmitAs(r);

        var runs = RunsOf(id);
        runs.Select(x => (x.StageCode, x.NodeId, x.NodeKind, x.BranchKey, x.InEdgeId, Name(x), x.Mode))
            .Should().Equal(("start", "start", "start", "", null, "", "any"), ("a", "a", "approval", "", "e0", "a", "any"));
    }

    [Fact]
    public async Task Submit_Condition_ChoosesByOrder()
    {
        var r = await Rig(CondByOrder);
        var (_, id, _) = await SubmitAs(r, price: 50000);   // a、b 的條件都成立：order 小的 b 先

        RunsOf(id).Select(x => x.NodeId).Should().Equal("start", "c", "b");
        RunsOf(id).Single(x => x.NodeId == "b").InEdgeId.Should().Be("cb");
        TasksOf(id).Select(t => t.AssigneeUserId).Should().Equal("buyer2");
        (await Task.FromResult(Inst(id).CurrentStageRunId)).Should().Be(RunsOf(id).Single(x => x.NodeId == "b").Id);
    }

    [Fact]
    public async Task Submit_Condition_OnlyLowerOrderHolds_ChoosesIt()
    {
        var r = await Rig(CondByOrder);
        var (_, id, _) = await SubmitAs(r, price: 5000);

        RunsOf(id).Select(x => x.NodeId).Should().Equal("start", "c", "a");
        RunsOf(id).Single(x => x.NodeId == "c").Status.Should().Be(ApprovalStageStatus.Passed);
    }

    [Fact]
    public async Task Submit_Condition_DefaultWhenNoneHolds()
    {
        var r = await Rig(CondByOrder);
        var (_, id, _) = await SubmitAs(r, price: 500);

        RunsOf(id).Select(x => (x.NodeId, x.InEdgeId)).Should().Equal(("start", null), ("c", "e0"), ("d", "cd"));
    }

    [Fact]
    public async Task Submit_ConditionDefaultToEnd_ApprovedImmediately_TransitionRuns()
    {
        var r = await Rig(CondToEnd);
        var (po, id, res) = await SubmitAs(r, price: 5000);

        res.Outcome.Should().Be(ApprovalOutcomes.Approved);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        StateOf(r, po).Should().Be(r.Confirmed);
        RunsOf(id).Select(x => (x.NodeId, x.Status)).Should().Equal(("start", "passed"), ("c", "passed"), ("end", "passed"));
        RunsOf(id).Last().InEdgeId.Should().Be("ce");
        TasksOf(id).Should().BeEmpty();
    }

    [Fact]
    public async Task Submit_GraphEnd_CompleteOnce()
    {
        // 簽核節點全部略過、直接走到結束：完成只發生一次（歷程 complete 一筆、發起人一則核准通知、轉移一次；Review Focus 9）
        var r = await Rig(r0 => G([Start(), Approval("a", Users("fin1"), when: Cond(r0.Total, "gt", 1000000)), End()],
            [E("e0", "start", "a"), E("e1", "a", "end")]));
        var (po, id, res) = await SubmitAs(r);

        res.Outcome.Should().Be(ApprovalOutcomes.Approved);
        Count(id, ApprovalActions.Complete).Should().Be(1);
        Notices("buyer1", NotificationKinds.ApprovalApproved).Should().Be(0);   // 送出的人就是發起人，不通知自己（同第一版）；同意收尾的一則見 Approve_GraphEnd_CompleteOnce
        StateOf(r, po).Should().Be(r.Confirmed);
        Inst(id).CurrentStageRunId.Should().BeNull();
    }

    [Fact]
    public async Task Submit_SkipCondition_SkippedWithHistory()
    {
        var r = await Rig(SkipFirst);
        var (_, id, _) = await SubmitAs(r);

        RunsOf(id).Select(x => (x.NodeId, x.Status, x.SkipReason)).Should().Equal(("start", "passed", null), ("a", "skipped", "condition"), ("b", "active", null));
        var skip = ActionsOf(id).Single(a => a.Action == ApprovalActions.Skip);
        skip.StageRunId.Should().Be(RunsOf(id).Single(x => x.NodeId == "a").Id);
        DataOf(skip).GetProperty("reason").GetString().Should().Be("condition");
        TasksOf(id).Select(t => t.AssigneeUserId).Should().Equal("buyer2");
    }

    /// <summary>a 的略過條件（總額 &gt; 1000000）不成立 → 略過 → b。</summary>
    private static ApprovalDefinition SkipFirst(PurchaseRig r) => G(
        [Start(), Approval("a", Users("fin1"), when: Cond(r.Total, "gt", 1000000)), Approval("b", Users("buyer2")), End()],
        [E("e0", "start", "a"), E("e1", "a", "b"), E("e2", "b", "end")]);

    [Fact]
    public async Task Submit_OnEmptySkip_SkippedEmpty()
    {
        // a 的來源只有發起人自己：政策不允許自簽時單位是空的，onEmpty = skip → 略過
        var r = await Rig(G(
            [Start(), Approval("a", Users("buyer1")) with { OnEmpty = ApprovalOnEmpty.Skip }, Approval("b", Users("buyer2")), End()],
            [E("e0", "start", "a"), E("e1", "a", "b"), E("e2", "b", "end")]));
        var (_, id, _) = await SubmitAs(r);

        RunsOf(id).Select(x => (x.NodeId, x.Status, x.SkipReason)).Should().Equal(("start", "passed", null), ("a", "skipped", "empty"), ("b", "active", null));
        DataOf(ActionsOf(id).Single(a => a.Action == ApprovalActions.Skip)).GetProperty("reason").GetString().Should().Be("empty");
    }

    [Fact]
    public async Task Submit_ResolveFailure_409_NothingWritten()
    {
        var r = await Rig(Linear() with { FallbackApprovers = Users("emp2") });
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "fin1" || u.UserId == "emp2").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        var rowVersion = Rec(po.Id).Row.RowVersion;

        var res = await Submit(po.Id, r.Confirmed);

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.NoApprover("a", "fin1").Resolve(null));
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Count().Should().Be(0);
        Db.Context.Set<ApprovalTask>().AsNoTracking().Count().Should().Be(0);
        (StateOf(r, po.Id), Rec(po.Id).Row.RowVersion).Should().Be((r.Draft, rowVersion));
    }

    // ───────── 並行 ─────────

    [Fact]
    public async Task Submit_Fork_BothBranchesActive_BranchKeysAreEdgeIds()
    {
        var r = await Rig(ForkJoin(ApprovalModes.All));
        var (_, id, res) = await SubmitAs(r);

        res.Outcome.Should().Be(ApprovalOutcomes.Submitted);
        RunsOf(id).Select(x => (x.NodeId, x.BranchKey, x.InEdgeId, x.Status))
            .Should().Equal(("start", "", null, "passed"), ("f", "", "e0", "passed"), ("a", "fa", "fa", "active"), ("b", "fb", "fb", "active"));
        TasksOf(id).Select(t => t.AssigneeUserId).Should().BeEquivalentTo(["fin1", "buyer2"]);
    }

    [Fact]
    public async Task Submit_NestedFork_BranchKeyPath()
    {
        var r = await Rig(Nested(ApprovalModes.All));
        var (_, id, _) = await SubmitAs(r);

        RunsOf(id).Select(x => (x.NodeId, x.BranchKey))
            .Should().Equal(("start", ""), ("f", ""), ("x", "fx"), ("f2", "ff2"), ("y", "ff2/f2y"), ("z", "ff2/f2z"));
    }

    [Fact]
    public async Task Submit_ForkExpansionOrder_IsDefinitionOrder()
    {
        // 出線在定義裡 fb 排在 fa 前面：b 的節點實例 Seq 比 a 小
        var def = ForkJoin(ApprovalModes.All);
        def = def with { Edges = [.. def.Edges!.OrderBy(e => e.Id == "fa" ? 1 : 0)] };
        var r = await Rig(def);
        var (_, id, _) = await SubmitAs(r);

        RunsOf(id).Where(x => x.NodeKind == "approval").Select(x => x.NodeId).Should().Equal("b", "a");
    }

    [Fact]
    public async Task Submit_EmptyBranchAllJoin_ArrivalCountsAsThatBranch()
    {
        // fork → join 直連的空分支：到達列的 InEdgeId 是直連線、BranchKey 是外層段；另一條分支簽完後匯合通過（Review Focus 10）
        var r = await Rig(EmptyArm(ApprovalModes.All, emptyFirst: true));
        var (_, id, _) = await SubmitAs(r);

        RunsOf(id).Select(x => (x.NodeId, x.BranchKey, x.InEdgeId, x.Status))
            .Should().Equal(("start", "", null, "passed"), ("f", "", "e0", "passed"), ("j", "", "fe", "waiting"), ("a", "fa", "fa", "active"));

        await MustApprove(id, "fin1");

        RunsOf(id).Where(x => x.NodeKind == "join").Select(x => (x.InEdgeId, x.Status)).Should().Equal(("fe", "passed"), ("aj", "passed"));
        RunsOf(id).Single(x => x.NodeId == "c").Status.Should().Be(ApprovalStageStatus.Active);
        Open(id, "gm").Should().NotBeNull();
    }

    [Fact]
    public async Task Submit_BranchConditionDirectToJoin_ArrivalCountsAsThatArm()
    {
        // 分支 1 的條件節點選預設線直接到匯合：到達列算在分支 1（fc），全部到齊繼續等分支 2；分支 2 簽完後只推進一次
        var r = await Rig(ConditionIntoJoin);
        var (_, id, res) = await SubmitAs(r, price: 5000);

        res.Outcome.Should().Be(ApprovalOutcomes.Submitted);
        var join = RunsOf(id).Where(x => x.NodeKind == "join").ToList();
        join.Select(x => (x.BranchKey, x.InEdgeId, x.Status)).Should().Equal(("", "cj", "waiting"));
        var g = ApprovalGraphRuntime.Build(DefinitionOf(id), ApprovalGraphMode.SupportedKinds, out _)!;
        g.ArmOf("j", join[0].InEdgeId!).Should().Be("fc");

        await MustApprove(id, "buyer2");

        RunsOf(id).Where(x => x.NodeKind == "join").Select(x => (x.InEdgeId, x.Status)).Should().Equal(("cj", "passed"), ("bj", "passed"));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        Count(id, ApprovalActions.Complete).Should().Be(1);
    }

    // ───────── 任一到齊 ─────────

    [Fact]
    public async Task Submit_AnyJoin_FirstBranchArrivesImmediately_LaterBranchesNotStarted_HistoryBranchCancelled()
    {
        var r = await Rig(EmptyArm(ApprovalModes.Any, emptyFirst: true));
        var (_, id, _) = await SubmitAs(r);

        RunsOf(id).Select(x => (x.NodeId, x.Status)).Should().Equal(("start", "passed"), ("f", "passed"), ("j", "passed"), ("c", "active"));
        TasksOf(id).Select(t => t.AssigneeUserId).Should().Equal("gm");   // 後面的分支（a）沒有展開，fin1 沒有任務
        var cancelled = ActionsOf(id).Where(a => a.Action == ApprovalActions.BranchCancelled).ToList();
        cancelled.Should().ContainSingle();
        cancelled[0].ActorUserId.Should().Be(ApprovalEngine.SystemActor);
        cancelled[0].StageRunId.Should().Be(RunsOf(id).Single(x => x.NodeId == "j").Id);
        cancelled[0].Comment.Should().Be("分支「（未命名分支）」已完成，其他分支自動取消");
        var data = DataOf(cancelled[0]);
        (data.GetProperty("forkId").GetString(), data.GetProperty("joinId").GetString(), data.GetProperty("arrived").GetString())
            .Should().Be(("f", "j", "fe"));
        data.GetProperty("cancelled").EnumerateArray().Select(e => e.GetString()).Should().Equal("fa");
        data.TryGetProperty("name", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Submit_AnyJoin_EmptyBranch_CancelsSiblingsNotItself()
    {
        var r = await Rig(EmptyArm(ApprovalModes.Any, emptyFirst: false));
        var (_, id, _) = await SubmitAs(r);

        // a 先展開、有任務；空分支到達後 a 被取消，到達列自己是 passed
        var runs = RunsOf(id);
        runs.Single(x => x.NodeId == "a").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == "branchCancelled" && x.CompletedTime != null);
        runs.Single(x => x.NodeId == "j").Should().Match<ApprovalStageRun>(x => x.Status == "passed" && x.CloseReason == null && x.InEdgeId == "fe");
        runs.Single(x => x.NodeId == "f").Status.Should().Be(ApprovalStageStatus.Passed);
        TasksOf(id).Single(t => t.AssigneeUserId == "fin1").Status.Should().Be(ApprovalTaskStatus.Closed);
    }

    [Fact]
    public async Task AnyJoin_LaterBranchArrivesImmediately_EarlierTasksClosed_NoPendingNotice()
    {
        // U7：先展開的分支已建任務，同一次推進裡被取消 → 負責人沒有收到待簽通知；匯合之後的 c 照常通知
        var r = await Rig(EmptyArm(ApprovalModes.Any, emptyFirst: false));
        var (_, id, res) = await SubmitAs(r);

        res.Outcome.Should().Be(ApprovalOutcomes.Submitted);
        TasksOf(id).Single(t => t.AssigneeUserId == "fin1").Status.Should().Be(ApprovalTaskStatus.Closed);
        Notices("fin1", NotificationKinds.ApprovalPending).Should().Be(0);
        Notices("gm", NotificationKinds.ApprovalPending).Should().Be(1);
        Count(id, ApprovalActions.BranchCancelled).Should().Be(1);
    }

    [Fact]
    public async Task Approve_AnyJoin_FirstArrival_CancelsOtherBranch_NamesLastApprovedNode()
    {
        var r = await Rig(ForkJoin(ApprovalModes.Any));
        var (_, id, _) = await SubmitAs(r);
        Notices("buyer2", NotificationKinds.ApprovalPending).Should().Be(1);

        await MustApprove(id, "buyer2");

        var runs = RunsOf(id);
        runs.Single(x => x.NodeId == "a").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == "branchCancelled");
        runs.Single(x => x.NodeId == "b").Status.Should().Be(ApprovalStageStatus.Approved);
        runs.Single(x => x.NodeId == "j").Should().Match<ApprovalStageRun>(x => x.Status == "passed" && x.BranchKey == "" && x.InEdgeId == "bj");
        runs.Single(x => x.NodeId == "c").Status.Should().Be(ApprovalStageStatus.Active);
        TasksOf(id).Single(t => t.AssigneeUserId == "fin1").Status.Should().Be(ApprovalTaskStatus.Closed);
        var note = ActionsOf(id).Single(a => a.Action == ApprovalActions.BranchCancelled);
        note.Comment.Should().Be("分支「b」已完成，其他分支自動取消");
        var data = DataOf(note);
        data.GetProperty("arrived").GetString().Should().Be("fb");
        data.GetProperty("cancelled").EnumerateArray().Select(e => e.GetString()).Should().Equal("fa");
        data.GetProperty("name").GetProperty("zh-TW").GetString().Should().Be("b");
        Notices("gm", NotificationKinds.ApprovalPending).Should().Be(1);
        Inst(id).CurrentStageRunId.Should().Be(runs.Single(x => x.NodeId == "c").Id);
    }

    [Fact]
    public async Task Submit_NestedAnyInsideAll_InnerCancelOnlyInnerSiblings()
    {
        var r = await Rig(Nested(ApprovalModes.Any));
        var (_, id, _) = await SubmitAs(r);

        await MustApprove(id, "buyer2");   // y：內層任一到齊通過

        var runs = RunsOf(id);
        runs.Single(x => x.NodeId == "z").Should().Match<ApprovalStageRun>(x => x.Status == "closed" && x.CloseReason == "branchCancelled");
        runs.Single(x => x.NodeId == "x").Status.Should().Be(ApprovalStageStatus.Active);   // 外層的兄弟分支不受影響
        runs.Where(x => x.NodeId == "j").Select(x => (x.BranchKey, x.InEdgeId, x.Status)).Should().Equal(("", "j2j", "waiting"));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        var note = DataOf(ActionsOf(id).Single(a => a.Action == ApprovalActions.BranchCancelled));
        (note.GetProperty("forkId").GetString(), note.GetProperty("joinId").GetString(), note.GetProperty("arrived").GetString())
            .Should().Be(("f2", "j2", "ff2/f2y"));
        note.GetProperty("cancelled").EnumerateArray().Select(e => e.GetString()).Should().Equal("ff2/f2z");

        await MustApprove(id, "fin1");

        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        RunsOf(id).Where(x => x.NodeId == "j").Select(x => x.Status).Should().Equal("passed", "passed");
        Count(id, ApprovalActions.BranchCancelled).Should().Be(1);
    }

    // ───────── 唯一完成者 ─────────

    [Fact]
    public async Task Approve_GraphEnd_CompleteOnce()
    {
        var r = await Rig(Linear());
        var (po, id, _) = await SubmitAs(r);

        var res = await Approve(id, "fin1");

        res.IsSuccess.Should().BeTrue(Dump(res));
        ((ApprovalActionResponse)res.Data!).Outcome.Should().Be(ApprovalOutcomes.Approved);
        Count(id, ApprovalActions.Complete).Should().Be(1);
        Notices("buyer1", NotificationKinds.ApprovalApproved).Should().Be(1);
        StateOf(r, po).Should().Be(r.Confirmed);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        Inst(id).CurrentStageRunId.Should().BeNull();
        RunsOf(id).Last().Should().Match<ApprovalStageRun>(x => x.NodeId == "end" && x.Status == "passed" && x.InEdgeId == "e1");
    }

    [Fact]
    public async Task Reroute_ToGraphAllSkipped_CompleteOnce()
    {
        // 簽核中金額變動，重新比對改走 graph 流程，而它的簽核節點全部略過、直接走到結束：重來的實例只完成一次（Review Focus 9）
        var r = await BuildPurchase();
        await SeedGraphFlow(r, "po-a", r.Confirmed, 1, G(
            [Start(), Approval("x", Users("fin1"), when: Cond(r.Total, "gt", 1000000)), End()],
            [E("e0", "start", "x"), E("e1", "x", "end")], when: Cond(r.Total, "gte", 100000)));
        await SeedFlow(r, "po-b", r.Confirmed, 2, Def(Cond(r.Total, "gte", 10000), Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        var (po, id, _) = await SubmitAs(r, price: 50000);
        As("buyer1");
        (await Send(new DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord.CreateRecordRequest(r.Extra,
            V((TitleId(r.Extra), "追加"), (r.ExtraRef, po), (r.ExtraAmount, 120000m))))).IsCreated.Should().BeTrue();

        var res = await Approve(id, "buyer2");

        res.IsSuccess.Should().BeTrue(Dump(res));
        ((ApprovalActionResponse)res.Data!).Outcome.Should().Be(ApprovalOutcomes.Approved);
        var fresh = Db.Context.Set<ApprovalInstance>().AsNoTracking().Single(i => i.RestartedFromInstanceId == id);
        fresh.Status.Should().Be(ApprovalInstanceStatus.Approved);
        Count(fresh.Id, ApprovalActions.Complete).Should().Be(1);
        Notices("buyer1", NotificationKinds.ApprovalApproved).Should().Be(1);
        StateOf(r, po).Should().Be(r.Confirmed);
    }

    // ───────── 每一列都帶進線（路線比較依賴它）─────────

    [Fact]
    public async Task Submit_EveryRunCarriesInEdgeId_ExceptStart()
    {
        // 走完巢狀並行：除了 start 以外每一列的 InEdgeId 都存在、而且終點就是它自己的節點
        var r = await Rig(Nested(ApprovalModes.Any));
        var (_, id, _) = await SubmitAs(r);
        await MustApprove(id, "buyer2");
        await MustApprove(id, "fin1");

        var g = ApprovalGraphRuntime.Build(DefinitionOf(id), ApprovalGraphMode.SupportedKinds, out _)!;
        var runs = RunsOf(id);
        runs.Should().NotBeEmpty();
        foreach (var run in runs.Where(x => x.NodeKind != "start"))
        {
            run.InEdgeId.Should().NotBeNull($"{run.NodeId} 必須帶進線");
            g.Edge(run.InEdgeId!).To.Should().Be(run.NodeId);
        }
        runs.Single(x => x.NodeKind == "start").InEdgeId.Should().BeNull();
    }

    /// <summary>以「某條件的原文 → 結果」建 eval；沒列到的條件一律不成立。</summary>
    private static Func<JsonElement, ConditionTruth> EvalOf(params (JsonElement When, ConditionTruth Truth)[] table) =>
        when => table.FirstOrDefault(t => t.When.GetRawText() == when.GetRawText()) is { When.ValueKind: not JsonValueKind.Undefined } hit ? hit.Truth : ConditionTruth.Fails;

    [Fact]
    public async Task Route_ConditionToApproval_ChangedFindsChosenEdge()
    {
        var r = await Rig(CondByOrder);
        var (_, id, _) = await SubmitAs(r, price: 5000);   // 選了 ca（≥ 1000）
        var g = ApprovalGraphRuntime.Build(DefinitionOf(id), ApprovalGraphMode.SupportedKinds, out _)!;
        var big = Cond(r.Total, "gte", 10000);
        var mid = Cond(r.Total, "gte", 1000);

        ApprovalGraphRoute.Changed(g, RunsOf(id), EvalOf((big, ConditionTruth.Fails), (mid, ConditionTruth.Holds))).Changed.Should().BeFalse();
        ApprovalGraphRoute.Changed(g, RunsOf(id), EvalOf((big, ConditionTruth.Holds), (mid, ConditionTruth.Holds))).Changed.Should().BeTrue();
    }

    [Fact]
    public async Task Route_ConditionToEnd_ChangedFindsChosenEdge()
    {
        var r = await Rig(CondToEnd);
        var (_, id, _) = await SubmitAs(r, price: 5000);   // 預設線直接到 end
        var g = ApprovalGraphRuntime.Build(DefinitionOf(id), ApprovalGraphMode.SupportedKinds, out _)!;
        var big = Cond(r.Total, "gte", 10000);

        ApprovalGraphRoute.Changed(g, RunsOf(id), EvalOf((big, ConditionTruth.Fails))).Changed.Should().BeFalse();
        ApprovalGraphRoute.Changed(g, RunsOf(id), EvalOf((big, ConditionTruth.Holds))).Changed.Should().BeTrue();
    }

    [Fact]
    public async Task Route_ConditionDirectToJoin_ChangedFindsChosenEdgeFromArrivalRow()
    {
        var r = await Rig(ConditionIntoJoin);
        var (_, id, _) = await SubmitAs(r, price: 5000);   // 預設線 cj 直接接匯合，下一列是到達列
        var g = ApprovalGraphRuntime.Build(DefinitionOf(id), ApprovalGraphMode.SupportedKinds, out _)!;
        var huge = Cond(r.Total, "gte", 100000);

        ApprovalGraphRoute.Changed(g, RunsOf(id), EvalOf((huge, ConditionTruth.Fails))).Changed.Should().BeFalse();
        ApprovalGraphRoute.Changed(g, RunsOf(id), EvalOf((huge, ConditionTruth.Holds))).Changed.Should().BeTrue();
    }

    [Fact]
    public async Task Route_SkippedApprovalWhenNowHolds_Changed()
    {
        var r = await Rig(SkipFirst);
        var (_, id, _) = await SubmitAs(r);
        var g = ApprovalGraphRuntime.Build(DefinitionOf(id), ApprovalGraphMode.SupportedKinds, out _)!;
        var skipWhen = Cond(r.Total, "gt", 1000000);

        ApprovalGraphRoute.Changed(g, RunsOf(id), EvalOf((skipWhen, ConditionTruth.Fails))).Changed.Should().BeFalse();
        ApprovalGraphRoute.Changed(g, RunsOf(id), EvalOf((skipWhen, ConditionTruth.Holds))).Changed.Should().BeTrue();
    }

    // ───────── 異常：結構、能力守門、條件無法判斷 ─────────

    private async Task<(PurchaseRig R, long Po, int RunsBefore)> SubmitExpect409(ApprovalDefinition def, string message)
    {
        var r = await Rig(def);
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        var rowVersion = Rec(po.Id).Row.RowVersion;

        var res = await Submit(po.Id, r.Confirmed);

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(message);
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Count().Should().Be(0);
        Db.Context.Set<ApprovalTask>().AsNoTracking().Count().Should().Be(0);
        (StateOf(r, po.Id), Rec(po.Id).Row.RowVersion).Should().Be((r.Draft, rowVersion));
        Notices("admin", NotificationKinds.ApprovalAdminAlert).Should().Be(1);
        return (r, po.Id, 0);
    }

    [Fact]
    public async Task Submit_ConditionUndecidable_409_AlertWrittenAfterRollback()
    {
        // 條件節點引用的欄位已刪除：409、沒有實例、主交易回滾後另開交易通知系統管理員（Review Focus 6、J20）
        var def = G([Start(), Condition("c"), Approval("a", Users("fin1")), End()],
            [E("e0", "start", "c"), E("ca", "c", "a", Cond(999999, "gte", 1), order: 1), E("cd", "c", "end", isDefault: true), E("ae", "a", "end")]);
        var (_, po, _) = await SubmitExpect409(def, ApprovalMessages.ConditionNodeUndecidable("c").Resolve(null)!);

        var alert = Db.Context.Set<Notification>().AsNoTracking().Single(n => n.Kind == NotificationKinds.ApprovalAdminAlert);
        alert.UserId.Should().Be("admin");
        alert.TitleSnapshot.Should().StartWith("單據：");
        alert.Link.Should().EndWith($"/{po}");
    }

    [Fact]
    public async Task Submit_BrokenGraph_409_AlertWritten()
    {
        // 缺預設線（繞過驗證寫入）：訊息是 GraphRuntimeInvalid，不是第一版的 RuntimeInvalid
        var def = G([Start(), Condition("c"), Approval("a", Users("fin1")), End()],
            [E("e0", "start", "c"), E("ca", "c", "a", Cond(1, "gte", 1), order: 1), E("cb", "c", "end", Cond(2, "gte", 1), order: 2), E("ae", "a", "end")]);
        await SubmitExpect409(def, ApprovalMessages.GraphRuntimeInvalid.Resolve(null)!);
        ApprovalMessages.GraphRuntimeInvalid.Resolve(null).Should().NotBe(ApprovalMessages.RuntimeInvalid.Resolve(null));
    }

    [Fact]
    public async Task Submit_NotifyNode_409_Unsupported_AlertWritten_NothingWritten()
    {
        var def = G([Start(), Notify("tell"), End()], [E("e0", "start", "tell"), E("e1", "tell", "end")]);
        await SubmitExpect409(def, ApprovalMessages.GraphRuntimeInvalid.Resolve(null)!);
    }

    [Fact]
    public async Task RuntimeInvalid_NoMessage_SequenceMessageUnchanged()
    {
        var r = await Rig(Linear());
        var (_, id, _) = await SubmitAs(r);
        using var scope = NewScope();
        var engine = scope.ServiceProvider.GetRequiredService<ApprovalEngine>();
        var inst = Inst(id);

        engine.RuntimeInvalid(inst, "x").Message.Should().Be(ApprovalMessages.RuntimeInvalid.Resolve(null));
        engine.RuntimeInvalid(inst, "x", ApprovalMessages.GraphRuntimeInvalid).Message.Should().Be(ApprovalMessages.GraphRuntimeInvalid.Resolve(null));
    }

    [Fact]
    public async Task Advance_EndWithLiveRun_RuntimeInvalidWithGraphMessage()
    {
        // 引擎內部不變式（G8 的防線，S4）：走到結束時還有進行中的執行線 → 409＋異常通知，訊息是 GraphRuntimeInvalid；手寫一個孤兒列製造
        var r = await Rig(Linear());
        var (_, id, _) = await SubmitAs(r);
        await AddRows(new ApprovalStageRun
        {
            InstanceId = id, Seq = 99, StageCode = "stray", NodeId = "stray", NodeKind = "approval", BranchKey = "stray", InEdgeId = "e1",
            Status = ApprovalStageStatus.Active, Mode = ApprovalModes.Any
        });
        var before = (Inst(id).RowVersion, ActionsOf(id).Count);

        var res = await Approve(id, "fin1");

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.GraphRuntimeInvalid.Resolve(null));
        (Inst(id).RowVersion, ActionsOf(id).Count).Should().Be(before);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        Open(id, "fin1").Status.Should().Be(ApprovalTaskStatus.Open);
        Notices("admin", NotificationKinds.ApprovalAdminAlert).Should().Be(1);
    }

    // ───────── 自選、目前關卡、加簽 ─────────

    private static ApprovalDefinition PickOffPath(PurchaseRig r, long group) => G(
        [Start(), Condition("c"), Approval("p", new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [group], Min: 1, Max: 1)), End()],
        [E("e0", "start", "c"), E("cp", "c", "p", Cond(r.Total, "gte", 100000), order: 1), E("cd", "c", "end", isDefault: true), E("pe", "p", "end")]);

    [Fact]
    public async Task Submit_PickRequiredForEveryPickNode_EvenOffPath()
    {
        // 設計 §8.9、J25：定義中所有自選節點都要選，不論這次的路徑有沒有經過
        var r = await Rig(r0 => PickOffPath(r0, FinanceGroup));
        As("buyer1");
        var po = await NewPo(r, price: 5000);

        var missing = await Submit(po.Id, r.Confirmed);
        missing.Errors.Should().ContainKey("picks.p");
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);

        var wrong = await Submit(po.Id, r.Confirmed, new() { ["zz"] = ["fin1"] });
        wrong.Errors.Should().ContainKey("picks.zz");

        var ok = await Submit(po.Id, r.Confirmed, new() { ["p"] = ["fin1"] });
        ok.IsCreated.Should().BeTrue(Dump(ok));
        ((SubmitApprovalResponse)ok.Data!).Outcome.Should().Be(ApprovalOutcomes.Approved);   // 預設線直接到 end
        Inst(((SubmitApprovalResponse)ok.Data!).InstanceId!.Value).Picks["p"].Should().Equal("fin1");
    }

    [Fact]
    public async Task Submit_PickNodeOnPath_UsesPickedUsers()
    {
        var r = await Rig(r0 => PickOffPath(r0, FinanceGroup));
        As("buyer1");
        var po = await NewPo(r, price: 150000);
        var res = await Submit(po.Id, r.Confirmed, new() { ["p"] = ["fin2"] });

        res.IsCreated.Should().BeTrue(Dump(res));
        TasksOf(((SubmitApprovalResponse)res.Data!).InstanceId!.Value).Select(t => t.AssigneeUserId).Should().Equal("fin2");
    }

    [Fact]
    public async Task Submit_CurrentStageRunId_IsMinSeqActive()
    {
        var r = await Rig(ForkJoin(ApprovalModes.All));
        var (_, id, _) = await SubmitAs(r);
        var runs = RunsOf(id);
        Inst(id).CurrentStageRunId.Should().Be(runs.Single(x => x.NodeId == "a").Id);

        await MustApprove(id, "fin1");
        Inst(id).CurrentStageRunId.Should().Be(RunsOf(id).Single(x => x.NodeId == "b").Id);

        await MustApprove(id, "buyer2");
        Inst(id).CurrentStageRunId.Should().Be(RunsOf(id).Single(x => x.NodeId == "c").Id);

        await MustApprove(id, "gm");
        Inst(id).CurrentStageRunId.Should().BeNull();
    }

    [Fact]
    public async Task Submit_AddSignQueuedInOtherBranch_NotActivated()
    {
        // J11：本分支節點完成時，只看同段同主節點的排隊加簽；另一條分支的不啟動
        var r = await Rig(ForkJoin(ApprovalModes.All));
        var (_, id, _) = await SubmitAs(r);
        await AddRows(new ApprovalStageRun
        {
            InstanceId = id, Seq = 99, StageCode = null, NodeId = "b", NodeKind = "approval", BranchKey = "fb", Status = ApprovalStageStatus.Queued,
            Mode = ApprovalModes.Any, AddedByUserId = "buyer2", AddSignUserIds = ["emp1"]
        });

        await MustApprove(id, "fin1");

        RunsOf(id).Single(x => x.Seq == 99).Status.Should().Be(ApprovalStageStatus.Queued);
        TasksOf(id).Should().NotContain(t => t.AssigneeUserId == "emp1");
        RunsOf(id).Single(x => x.NodeId == "j").Should().Match<ApprovalStageRun>(x => x.Status == "waiting" && x.InEdgeId == "aj");
    }

    [Fact]
    public async Task Approve_QueuedAddSignSameNode_StartsBeforeAdvancing()
    {
        var r = await Rig(Linear());
        var (_, id, _) = await SubmitAs(r);
        await AddRows(new ApprovalStageRun
        {
            InstanceId = id, Seq = 99, StageCode = null, NodeId = "a", NodeKind = "approval", BranchKey = "", Status = ApprovalStageStatus.Queued,
            Mode = ApprovalModes.Any, AddedByUserId = "fin1", AddSignUserIds = ["emp1"]
        });

        await MustApprove(id, "fin1");

        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        RunsOf(id).Single(x => x.Seq == 99).Status.Should().Be(ApprovalStageStatus.Active);
        TasksOf(id).Single(t => t.AssigneeUserId == "emp1").Status.Should().Be(ApprovalTaskStatus.Open);
        Notices("emp1", NotificationKinds.ApprovalAddSigned).Should().Be(1);

        await MustApprove(id, "emp1");

        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        Count(id, ApprovalActions.Complete).Should().Be(1);
    }

    // ───────── 異常通知 ─────────

    [Fact]
    public async Task WriteAlert_InstanceGone_UsesSnapshot()
    {
        var r = await Rig(Linear());
        As("buyer1");
        var po = await NewPo(r);
        As("admin");

        var res = await Send(new WriteApprovalAlertRequest(987654, "原因", new ApprovalAlertSnapshot(po.Id, r.ModuleId, "單據")));

        res.IsSuccess.Should().BeTrue(Dump(res));
        var n = Db.Context.Set<Notification>().AsNoTracking().Single(x => x.Kind == NotificationKinds.ApprovalAdminAlert);
        (n.UserId, n.TitleSnapshot, n.Link, n.SourceId).Should().Be(("admin", "單據：原因", $"/dynamic/{r.Module}/{po.Id}", 987654L));
    }

    [Fact]
    public async Task WriteAlert_InstanceGoneNoSnapshot_NotFound()
    {
        As("admin");
        var res = await Send(new WriteApprovalAlertRequest(987654, "原因"));
        res.IsSuccess.Should().BeFalse();
        Db.Context.Set<Notification>().AsNoTracking().Count(x => x.Kind == NotificationKinds.ApprovalAdminAlert).Should().Be(0);
    }

    // ───────── fg2b 審查修正 ─────────

    [Fact]
    public async Task Approve_NestedAnyJoin_HistoryNameIsArrivedPath_NotCancelledInnerBranch()
    {
        // U8：外層任一到齊 f（fa → 內層任一到齊 f2；fb → y）。f2z → z（先展開）、f2s → s（略過）→ x。
        // z 核准：內層 j2 通過（取消 x）、外層 j 通過（取消 y）；外層歷程的名稱是到達路徑上的 z，不是被取消的內層分支上略過的 s
        var r = await Rig(r0 => G(
            [Start(), Fork("f"), Fork("f2"), Approval("z", Users("fin1")), Approval("s", Users("gm"), when: Cond(r0.Total, "gt", 1000000)),
             Approval("x", Users("emp1")), Join("j2", "f2", ApprovalModes.Any), Approval("y", Users("buyer2")), Join("j", "f", ApprovalModes.Any), End()],
            [E("e0", "start", "f"), E("fa", "f", "f2"), E("fb", "f", "y"), E("f2z", "f2", "z"), E("f2s", "f2", "s"), E("sx", "s", "x"),
             E("zj2", "z", "j2"), E("xj2", "x", "j2"), E("j2j", "j2", "j"), E("yj", "y", "j"), E("je", "j", "end")]));
        var (_, id, _) = await SubmitAs(r);
        RunsOf(id).Single(x => x.NodeId == "s").Status.Should().Be(ApprovalStageStatus.Skipped);

        await MustApprove(id, "fin1");

        var notes = ActionsOf(id).Where(a => a.Action == ApprovalActions.BranchCancelled).OrderBy(a => a.Id).ToList();
        notes.Select(a => a.Comment).Should().Equal("分支「z」已完成，其他分支自動取消", "分支「z」已完成，其他分支自動取消");
        DataOf(notes[1]).GetProperty("name").GetProperty("zh-TW").GetString().Should().Be("z");
    }

    [Fact]
    public async Task Approve_AnyJoin_NameTakenFromSkippedLastNodeOnArrivedPath()
    {
        // U8：到達分支最後一個「已核准或略過」的簽核節點；fa：a → s（略過）→ j，核准 a 後 s 略過、到達，名稱取 s
        var r = await Rig(r0 => G(
            [Start(), Fork("f"), Approval("a", Users("fin1")), Approval("s", Users("gm"), when: Cond(r0.Total, "gt", 1000000)),
             Approval("b", Users("buyer2")), Join("j", "f", ApprovalModes.Any), End()],
            [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("as", "a", "s"), E("sj", "s", "j"), E("bj", "b", "j"), E("je", "j", "end")]));
        var (_, id, _) = await SubmitAs(r);

        await MustApprove(id, "fin1");

        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        ActionsOf(id).Single(a => a.Action == ApprovalActions.BranchCancelled).Comment.Should().Be("分支「s」已完成，其他分支自動取消");
    }

    [Fact]
    public async Task Approve_AnyJoin_ClosesNestedForkInsideCancelledArm()
    {
        // §8.3：外層任一到齊 f（fa → a；fb → 內層全部到齊 f2 的 p、q）。p 先核准，j2 有一列 waiting；a 核准後被取消的 fb 內 q、j2 也要關閉、任務關掉
        var r = await Rig(_ => G(
            [Start(), Fork("f"), Approval("a", Users("fin1")), Fork("f2"), Approval("p", Users("gm")), Approval("q", Users("emp1")),
             Join("j2", "f2"), Join("j", "f", ApprovalModes.Any), End()],
            [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "f2"), E("f2p", "f2", "p"), E("f2q", "f2", "q"),
             E("pj2", "p", "j2"), E("qj2", "q", "j2"), E("aj", "a", "j"), E("j2j", "j2", "j"), E("je", "j", "end")]));
        var (_, id, _) = await SubmitAs(r);
        await MustApprove(id, "gm");
        RunsOf(id).Single(x => x.NodeId == "j2").Status.Should().Be(ApprovalStageStatus.Waiting);

        await MustApprove(id, "fin1");

        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        RunsOf(id).Where(x => x.NodeId is "q" or "j2").Should()
            .OnlyContain(x => x.Status == ApprovalStageStatus.Closed && x.CloseReason == ApprovalCloseReasons.BranchCancelled);
        TasksOf(id).Single(t => t.AssigneeUserId == "emp1").Status.Should().Be(ApprovalTaskStatus.Closed);
    }

    [Fact]
    public async Task Approve_DeferredNotice_StillExceptsActor()
    {
        // 偏離 1 的 ExceptUserId：fin1 核准 a 後啟動的 b 也有 fin1，延後寫的待簽通知仍不通知動作者本人
        var r = await Rig(G([Start(), Approval("a", Users("fin1")), Approval("b", Users("fin1", "gm")), End()],
            [E("e0", "start", "a"), E("ab", "a", "b"), E("be", "b", "end")]));
        var (_, id, _) = await SubmitAs(r);
        Notices("fin1", NotificationKinds.ApprovalPending).Should().Be(1);

        await MustApprove(id, "fin1");

        TasksOf(id).Should().Contain(t => t.AssigneeUserId == "fin1" && t.Status == ApprovalTaskStatus.Open);
        Notices("fin1", NotificationKinds.ApprovalPending).Should().Be(1);
        Notices("gm", NotificationKinds.ApprovalPending).Should().Be(1);
    }

    [Fact]
    public async Task Approve_QueuedAddSignWithoutNodeId_409_RuntimeInvalid_NothingChanged()
    {
        // 暫時防線：graph 的加簽寫入在 fg2d 才完整，排隊列沒有 NodeId 不能默默跳過（少簽，a → b 會直接啟動 b），改回 409＋異常通知
        var r = await Rig(G([Start(), Approval("a", Users("fin1")), Approval("b", Users("buyer2")), End()],
            [E("e0", "start", "a"), E("ab", "a", "b"), E("be", "b", "end")]));
        var (_, id, _) = await SubmitAs(r);
        await AddRows(new ApprovalStageRun
        {
            InstanceId = id, Seq = 99, StageCode = null, NodeId = null, NodeKind = "approval", BranchKey = null, Status = ApprovalStageStatus.Queued,
            Mode = ApprovalModes.Any, AddedByUserId = "fin1", AddSignUserIds = ["emp1"]
        });
        var before = (Inst(id).RowVersion, ActionsOf(id).Count, RunsOf(id).Count);

        var res = await Approve(id, "fin1");

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.GraphRuntimeInvalid.Resolve(null));
        (Inst(id).RowVersion, ActionsOf(id).Count, RunsOf(id).Count).Should().Be(before);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        Open(id, "fin1").Status.Should().Be(ApprovalTaskStatus.Open);
        Notices("admin", NotificationKinds.ApprovalAdminAlert).Should().Be(1);
    }

    [Fact]
    public async Task Approve_QueuedAddSignSameSegmentOtherNode_NotStarted()
    {
        // J11：同段但主節點不同（排的是 b）的加簽，a 完成時不啟動；照常往下走到 b
        var r = await Rig(G([Start(), Approval("a", Users("fin1")), Approval("b", Users("buyer2")), End()],
            [E("e0", "start", "a"), E("ab", "a", "b"), E("be", "b", "end")]));
        var (_, id, _) = await SubmitAs(r);
        await AddRows(new ApprovalStageRun
        {
            InstanceId = id, Seq = 99, StageCode = null, NodeId = "b", NodeKind = "approval", BranchKey = "", Status = ApprovalStageStatus.Queued,
            Mode = ApprovalModes.Any, AddedByUserId = "buyer2", AddSignUserIds = ["emp1"]
        });

        await MustApprove(id, "fin1");

        RunsOf(id).Single(x => x.Seq == 99).Status.Should().Be(ApprovalStageStatus.Queued);
        TasksOf(id).Should().NotContain(t => t.AssigneeUserId == "emp1");
        Open(id, "buyer2").Status.Should().Be(ApprovalTaskStatus.Open);
    }

    // ───────── sequence 實例回滾後警示照送 ─────────

    private async Task ReplaceTransitions(PurchaseRig r, Func<List<WorkflowTransition>, List<WorkflowTransition>> change)
    {
        var wf = Db.Context.Set<Workflow>().Single(w => w.ModuleId == r.ModuleId);
        wf.Transitions = change([.. wf.Transitions]);
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Alert_SequenceRerouteRolledBack_StillNotifiesAdmins()
    {
        // 重來路徑：追加單讓金額變動、改選另一條關卡全略過的 sequence 流程；轉移被拿掉所以新實例完成時再驗失敗，
        // 新實例隨主交易回滾而消失，警示靠快照照送（J20）
        var r = await BuildPurchase();
        await SeedFlow(r, "po-a", r.Confirmed, 1, Def(Cond(r.Total, "gte", 100000), Stage("x", Users("fin1"), when: Cond(r.Total, "gt", 1000000))));
        await SeedFlow(r, "po-b", r.Confirmed, 2, Def(Cond(r.Total, "gte", 10000), Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        As("buyer1");
        var po = await NewPo(r, price: 50000);
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        (await Send(new CreateRecordRequest(r.Extra, V((TitleId(r.Extra), "追加"), (r.ExtraRef, po.Id), (r.ExtraAmount, 120000m))))).IsCreated.Should().BeTrue();
        await ReplaceTransitions(r, t => t.Where(x => !(x.From == r.Draft && x.To == r.Confirmed)).ToList());

        var res = await Approve(id, "buyer2");

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.RuntimeInvalid.Resolve(null));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count(i => i.RestartedFromInstanceId == id).Should().Be(0);
        Notices("admin", NotificationKinds.ApprovalAdminAlert).Should().Be(1);
    }

    // ───────── DI ─────────

    [Fact]
    public void Engine_ResolvesFromDi_NoCycle()
    {
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ApprovalEngine>().Should().NotBeNull();
        sp.GetRequiredService<ApprovalGraphEngine>().Should().NotBeNull();
        sp.GetRequiredService<ApprovalApprover>().Should().NotBeNull();
        sp.GetRequiredService<IRequestHandler<SubmitApprovalRequest, ServiceResult>>().Should().NotBeNull();
        sp.GetRequiredService<IRequestHandler<ApproveTaskRequest, ServiceResult>>().Should().NotBeNull();
        sp.GetRequiredService<IRequestHandler<ApproveTaskRequest, ServiceResult>>().Should().NotBeNull();
        // J22：每個動作 handler 都解析得出來（含退回、撤回、終止、加簽、轉交、改派）
        var handlers = typeof(ApprovalEngine).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, Namespace: "DynamicModules.Application.Dynamic.ApprovalService" } && t.Name.EndsWith("Handler"))
            .SelectMany(t => t.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
            .ToList();
        handlers.Should().NotBeEmpty();
        foreach (var h in handlers) sp.GetRequiredService(h).Should().NotBeNull(h.ToString());
    }
}
