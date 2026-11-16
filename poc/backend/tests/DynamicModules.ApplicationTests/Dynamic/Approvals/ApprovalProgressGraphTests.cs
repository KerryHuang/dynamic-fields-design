using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 記錄簽核回應的進度圖（計畫 fg4 Task 4；J5–J7、J10、U1、U5、U7、U8、M1、M8）：graph、sequence 轉直線、備援三節點、只看自己關卡的遮蔽、壞資料回 null。
/// 簽核人：a＝fin1、a1＝fin1、b＝buyer2、c＝gm；發起人 buyer1。
/// </summary>
public class ApprovalProgressGraphTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // ───────── 圖與情境 ─────────

    /// <summary>start → fork f → 分支 a（fin1）、b（buyer2）→ join j → c（gm）→ end。</summary>
    private static ApprovalDefinition ForkJoin(string joinMode = ApprovalModes.All) => G(
        [Start(), Fork("f"), Approval("a", Users("fin1")), Approval("b", Users("buyer2")), Join("j", "f", joinMode), Approval("c", Users("gm")), End()],
        [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("jc", "j", "c"), E("ce", "c", "end")]);

    /// <summary>start → a（fin1）→ b（gm）→ end。</summary>
    private static ApprovalDefinition Line() => G(
        [Start(), Approval("a", Users("fin1")), Approval("b", Users("gm")), End()],
        [E("e1", "start", "a"), E("e2", "a", "b"), E("e3", "b", "end")]);

    /// <summary>start → 條件 c →（總額 ≥ 1）a（fin1，有逾期時數與可編輯欄位）／預設直接結束 → end：用來確認回應不帶條件與設定。</summary>
    private static ApprovalDefinition WithCondition(PurchaseRig r) => G(
        [Start(), Condition("c"), Approval("a", Users("fin1"), editable: [r.DueDate]) with { DueHours = 24 }, End()],
        [E("e0", "start", "c"), E("ca", "c", "a", Cond(r.Total, "gte", 1), order: 1), E("cd", "c", "end", isDefault: true), E("ae", "a", "end")]);

    /// <summary>外層 f（任一到齊）：fx 是 x（fin1）；fb 是 b1（buyer2）→ 內層 f2 → y（gm）、z（emp1）→ j2（全部）→ j。</summary>
    private static ApprovalDefinition NestedInCancelled() => G(
        [Start(), Fork("f"), Approval("x", Users("fin1")), Approval("b1", Users("buyer2")), Fork("f2"), Approval("y", Users("gm")), Approval("z", Users("emp1")),
         Join("j2", "f2"), Join("j", "f", ApprovalModes.Any), End()],
        [E("e0", "start", "f"), E("fx", "f", "x"), E("fb", "f", "b1"), E("b1f2", "b1", "f2"), E("f2y", "f2", "y"), E("f2z", "f2", "z"),
         E("yj2", "y", "j2"), E("zj2", "z", "j2"), E("j2j", "j2", "j"), E("xj", "x", "j"), E("je", "j", "end")]);

    private async Task<PurchaseRig> Rig(Func<PurchaseRig, ApprovalDefinition> graph)
    {
        var r = await BuildPurchase();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, graph(r));
        await ReadyWorkflow(r);
        return r;
    }

    private async Task ReadyWorkflow(PurchaseRig r)
    {
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
    }

    private async Task<(PurchaseRig R, long Id)> Boot(Func<PurchaseRig, ApprovalDefinition> graph, decimal price = 5000)
    {
        var r = await Rig(graph);
        As("buyer1");
        var po = await NewPo(r, price: price);
        return (r, (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value);
    }

    private Task<(PurchaseRig R, long Id)> Boot(ApprovalDefinition graph) => Boot(_ => graph);

    /// <summary>兩關的 sequence 流程：s1（fin1）→ s2（gm）。</summary>
    private async Task<(PurchaseRig R, long Id)> BootSequence()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-s", r.Confirmed, 1, Def(null, Stage("s1", Users("fin1")), Stage("s2", Users("gm"))));
        await ReadyWorkflow(r);
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        return (r, (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value);
    }

    private ApprovalTask Open(long id, string user) => TasksOf(id).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private async Task MustApprove(long id, string user)
    {
        As(user);
        var res = await Send(new ApproveTaskRequest(Open(id, user).Id, Inst(id).RowVersion, null, null));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private async Task MustAddSign(long id, string user, string to)
    {
        As(user);
        var res = await Send(new AddSignTaskRequest(Open(id, user).Id, Inst(id).RowVersion, null, null, [to], ApprovalModes.Any));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private async Task MustReject(long id, string user, string to)
    {
        As(user);
        var res = await Send(new RejectTaskRequest(Open(id, user).Id, Inst(id).RowVersion, "請重看", to));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private async Task<RecordApprovalResponse> RecordView(long recordId, string user)
    {
        As(user);
        var res = await Send(new GetRecordApprovalRequest(recordId));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (RecordApprovalResponse)res.Data!;
    }

    private async Task<ApprovalInstanceView> ViewAs(long id, string user)
    {
        var all = await RecordView(Inst(id).RecordId, user);
        return new[] { all.Current }.Concat(all.Previous).OfType<ApprovalInstanceView>().Single(v => v.Id == id);
    }

    /// <summary>收回全員的模組權限：之後只有持任務的簽核人靠臨時讀取權打開單據（只看得到自己的關卡）。</summary>
    private async Task RevokeRead(PurchaseRig r)
    {
        As("admin");
        await RevokeAll(r.Module);
    }

    private static ApprovalProgressNode Node(ApprovalInstanceView v, string id) => v.Graph!.Nodes.Single(n => n.Id == id);
    private static string State(ApprovalInstanceView v, string id) => Node(v, id).State;
    private static string[] Traversed(ApprovalInstanceView v) => [.. v.Graph!.Edges.Where(e => e.Traversed).Select(e => e.Id)];

    private async Task UpdateInstance(long id, Action<ApprovalInstance> change)
    {
        var inst = Db.Context.Set<ApprovalInstance>().Single(i => i.Id == id);
        change(inst);
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    private Task SetDefinition(long id, ApprovalDefinition definition) =>
        UpdateInstance(id, inst => inst.FlowSetSnapshot = [inst.FlowSetSnapshot.Single() with { Definition = definition }]);

    // ───────── graph：狀態、走過的線與回應內容 ─────────

    [Fact]
    public async Task Progress_Graph_ForkJoinAny_FinanceApproves_LegalCancelled_JoinPassed()
    {
        var (_, id) = await Boot(ForkJoin(ApprovalModes.Any));
        await MustApprove(id, "fin1");   // 任一到齊：b 被取消

        var view = await ViewAs(id, "buyer1");

        view.Graph!.Nodes.Select(n => n.Id).Should().Equal("start", "f", "a", "b", "j", "c", "end");
        State(view, "start").Should().Be(ApprovalProgressStates.Passed);
        State(view, "f").Should().Be(ApprovalProgressStates.Passed);
        State(view, "a").Should().Be(ApprovalProgressStates.Passed);
        State(view, "b").Should().Be(ApprovalProgressStates.Cancelled);
        State(view, "j").Should().Be(ApprovalProgressStates.Passed);
        State(view, "c").Should().Be(ApprovalProgressStates.Active);
        State(view, "end").Should().Be(ApprovalProgressStates.NotReached);
        Node(view, "j").JoinMode.Should().Be(ApprovalModes.Any);
        Node(view, "a").JoinMode.Should().BeNull();
        Traversed(view).Should().BeEquivalentTo("e0", "fa", "fb", "aj", "jc");
        view.Graph.Edges.Select(e => e.Id).Should().Equal("e0", "fa", "fb", "aj", "bj", "jc", "ce");
    }

    [Fact]
    public async Task Progress_Graph_JsonHasNoConditionOrApproverConfig()
    {
        var (_, id) = await Boot(WithCondition);

        var json = JsonSerializer.Serialize((await ViewAs(id, "buyer1")).Graph, Web);

        json.Should().Contain("\"nodes\"").And.Contain("\"edges\"").And.Contain("\"traversed\"");
        foreach (var key in new[] { "when", "approvers", "editableFields", "dueHours", "onEmpty", "order", "default", "template", "recipients" })
            json.Should().NotContain($"\"{key}\"");
    }

    [Fact]
    public async Task Progress_Graph_RunIdsPointToStages()
    {
        var (_, id) = await Boot(ForkJoin());
        await MustAddSign(id, "fin1", "emp1");

        var view = await ViewAs(id, "buyer1");

        var stageIds = view.Stages.Select(s => s.Id).ToHashSet();
        var withRuns = view.Graph!.Nodes.Where(n => n.RunIds is not null).ToList();
        withRuns.Should().NotBeEmpty();
        withRuns.SelectMany(n => n.RunIds!).Should().OnlyContain(rid => stageIds.Contains(rid));
        withRuns.Select(n => n.Kind).Should().OnlyContain(k => k == ApprovalNodeKinds.Approval || k == ApprovalProgressKinds.AddSign);
        view.Graph.Nodes.Where(n => n.Kind is ApprovalNodeKinds.Start or ApprovalNodeKinds.Fork or ApprovalNodeKinds.Join or ApprovalNodeKinds.End)
            .Should().OnlyContain(n => n.RunIds == null);
    }

    [Fact]
    public async Task Progress_Graph_AddSign_IsNodeWithHostAndState()
    {
        var (_, id) = await Boot(ForkJoin());
        await MustAddSign(id, "fin1", "emp1");

        var view = await ViewAs(id, "buyer1");

        var small = view.Graph!.Nodes.Single(n => n.Kind == ApprovalProgressKinds.AddSign);
        small.HostId.Should().Be("a");
        small.State.Should().Be(ApprovalProgressStates.Active);
        small.Name.Should().BeNull();
        small.RunIds.Should().ContainSingle();
        view.Graph.Nodes.Select(n => n.Id).Should().ContainInOrder("a", small.Id, "b");   // 緊接在主節點後面
        State(view, "a").Should().Be(ApprovalProgressStates.Passed);
        var chain = view.Graph.Edges.Single(e => e.To == small.Id);
        (chain.From, chain.Traversed).Should().Be(("a", true));
        view.Graph.Edges.Single(e => e.Id == "aj").From.Should().Be(small.Id);   // 主節點的出線改從小節點出發
    }

    // ───────── sequence 轉直線圖、備援 ─────────

    [Fact]
    public async Task Progress_Sequence_InFlight_StraightLine_SecondStageActive()
    {
        var (_, id) = await BootSequence();
        await MustApprove(id, "fin1");

        var view = await ViewAs(id, "buyer1");

        view.Graph!.Nodes.Select(n => n.Id).Should().Equal("start", "s1", "s2", "end");
        view.Graph.Nodes.Select(n => n.State).Should().Equal("passed", "passed", "active", "notReached");
        view.Graph.Edges.Select(e => (e.From, e.To)).Should().Equal(("start", "s1"), ("s1", "s2"), ("s2", "end"));
        Traversed(view).Should().Equal("e1", "e2");
        Node(view, "s2").RunIds.Should().Equal(view.Stages.Single(s => s.StageCode == "s2").Id);
    }

    [Fact]
    public async Task Progress_Sequence_Approved_EndPassed()
    {
        var (_, id) = await BootSequence();
        await MustApprove(id, "fin1");
        await MustApprove(id, "gm");

        var view = await ViewAs(id, "buyer1");

        view.Status.Should().Be(ApprovalInstanceStatus.Approved);
        view.Graph!.Nodes.Select(n => n.State).Should().OnlyContain(s => s == ApprovalProgressStates.Passed);
        Traversed(view).Should().Equal("e1", "e2", "e3");
    }

    private async Task AddExtra(PurchaseRig r, long poId, decimal amount)
    {
        As("buyer1");
        var res = await Send(new CreateRecordRequest(r.Extra, V((TitleId(r.Extra), "追加"), (r.ExtraRef, poId), (r.ExtraAmount, amount))));
        res.IsCreated.Should().BeTrue(Dump(res));
    }

    private ApprovalInstance PendingOf(long recordId) =>
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Single(i => i.RecordId == recordId && i.Status == ApprovalInstanceStatus.Pending);

    [Fact]
    public async Task Progress_Fallback_SequenceFlow_StartFallbackEnd()
    {
        // 送出後金額被追加費用壓低，沒有任何流程符合：改由備援簽核人處理（U8）
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 50000);
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        await AddExtra(r, po.Id, -45000);
        await MustApprove(id, "buyer2");
        var fresh = PendingOf(po.Id);
        fresh.RouteKind.Should().Be(ApprovalRouteKind.Fallback);

        var view = await ViewAs(fresh.Id, "buyer1");

        view.Graph!.Nodes.Select(n => (n.Id, n.State)).Should().Equal(("start", "passed"), ("_fallback", "active"), ("end", "notReached"));
        Node(view, "_fallback").Name!.Resolve("zh-TW").Should().Be(ApprovalMessages.FallbackStageName.Resolve("zh-TW"));
        Traversed(view).Should().Equal("e1");
        // 被重新比對的舊實例照它自己的流程畫：lead 同意過，之後被關閉
        (await ViewAs(id, "buyer1")).Graph!.Nodes.Select(n => n.Id).Should().Equal("start", "lead", "end");
    }

    [Fact]
    public async Task Progress_Fallback_GraphFlow_StartFallbackEnd_NotOldGraph()
    {
        // 建議 1：備援實例綁的是 graph 版本時，也要先看路線類型；FromSequence 對 graph 原樣回傳，會畫出整張沒走的舊圖
        var graph = (PurchaseRig r) => G(
            [Start(), Approval("a", Users("fin1")), Approval("b", Users("gm")), End()],
            [E("e1", "start", "a"), E("e2", "a", "b"), E("e3", "b", "end")], when: Cond(r.Total, "gte", 10000));
        var (rig, id) = await Boot(graph, price: 50000);
        var recordId = Inst(id).RecordId;
        await AddExtra(rig, recordId, -45000);
        await MustApprove(id, "fin1");
        var fresh = PendingOf(recordId);
        fresh.RouteKind.Should().Be(ApprovalRouteKind.Fallback);

        var view = await ViewAs(fresh.Id, "buyer1");

        view.Graph!.Nodes.Select(n => n.Id).Should().Equal("start", "_fallback", "end");
        State(view, "_fallback").Should().Be(ApprovalProgressStates.Active);
    }

    // ───────── 真流程：引擎行為的假設（Domain 測試用手造資料，這裡守住） ─────────

    [Fact]
    public async Task Progress_Sequence_ReturnPreviousToAddSign_RestartedJoinsSameSmallNode()
    {
        // s1 加簽 emp1、emp1 同意、s2 退回上一關 → 引擎重建 emp1 的加簽（複製加簽人、對象、方式）：兩列同一個小節點，掛在 s1
        var (_, id) = await BootSequence();
        await MustAddSign(id, "fin1", "emp1");
        await MustApprove(id, "emp1");
        await MustReject(id, "gm", RejectTaskHandler.ToPrevious);

        var view = await ViewAs(id, "buyer1");

        var small = view.Graph!.Nodes.Single(n => n.Kind == ApprovalProgressKinds.AddSign);
        small.HostId.Should().Be("s1");
        small.RunIds.Should().HaveCount(2);
        small.State.Should().Be(ApprovalProgressStates.Active);
        State(view, "s2").Should().Be(ApprovalProgressStates.Returned);

        await MustApprove(id, "emp1");
        var again = await ViewAs(id, "buyer1");
        again.Graph!.Nodes.Single(n => n.Kind == ApprovalProgressKinds.AddSign).State.Should().Be(ApprovalProgressStates.Passed);
        again.Graph.Nodes.Count(n => n.Kind == ApprovalProgressKinds.AddSign).Should().Be(1);
        State(again, "s2").Should().Be(ApprovalProgressStates.Active);
        Node(again, "s2").RunIds.Should().HaveCount(2);

        await MustApprove(id, "gm");
        (await ViewAs(id, "buyer1")).Graph!.Edges.Should().OnlyContain(e => e.Traversed);
    }

    [Fact]
    public async Task Progress_Graph_CancelledBranch_NestedForkNotEntered_AllUnreachedCancelled()
    {
        var (_, id) = await Boot(NestedInCancelled());
        await MustApprove(id, "fin1");   // 外層任一到齊時，fb 分支的 b1 還沒簽

        var view = await ViewAs(id, "buyer1");

        view.Status.Should().Be(ApprovalInstanceStatus.Approved);
        foreach (var n in new[] { "b1", "f2", "y", "z", "j2" }) State(view, n).Should().Be(ApprovalProgressStates.Cancelled, n);
        State(view, "j").Should().Be(ApprovalProgressStates.Passed);
        State(view, "end").Should().Be(ApprovalProgressStates.Passed);
    }

    [Fact]
    public async Task Progress_Graph_CancelledBranch_NestedForkEntered_RealFlow()
    {
        // 內層已進入（BranchKey 帶斜線）：y 已簽過、z 被一併取消，沒走到的 j2 也標已取消
        var (_, id) = await Boot(NestedInCancelled());
        await MustApprove(id, "buyer2");
        await MustApprove(id, "gm");
        await MustApprove(id, "fin1");

        var view = await ViewAs(id, "buyer1");

        State(view, "b1").Should().Be(ApprovalProgressStates.Passed);
        State(view, "f2").Should().Be(ApprovalProgressStates.Passed);
        State(view, "y").Should().Be(ApprovalProgressStates.Passed);
        State(view, "z").Should().Be(ApprovalProgressStates.Cancelled);
        State(view, "j2").Should().Be(ApprovalProgressStates.Cancelled);
        State(view, "j").Should().Be(ApprovalProgressStates.Passed);
    }

    // ───────── 只看得到自己關卡的讀者（U1、J7） ─────────

    [Fact]
    public async Task Progress_OwnStageReader_OnlyOwnNodeHasState_EdgesNotTraversed()
    {
        var (r, id) = await Boot(ForkJoin());
        await RevokeRead(r);

        var view = await ViewAs(id, "fin1");

        view.Graph!.Nodes.Select(n => n.Id).Should().Equal("start", "f", "a", "b", "j", "c", "end");   // 結構照給
        Node(view, "b").Name!.Resolve("zh-TW").Should().Be("b");
        State(view, "a").Should().Be(ApprovalProgressStates.Active);
        Node(view, "a").RunIds.Should().Equal(view.Stages.Single().Id);
        view.Graph.Nodes.Where(n => n.Id != "a").Should().OnlyContain(n => n.State == ApprovalProgressStates.Hidden && n.RunIds == null);
        view.Graph.Edges.Should().OnlyContain(e => !e.Traversed);
        view.Graph.Edges.Should().HaveCount(7);
    }

    [Fact]
    public async Task Progress_OwnStageReader_IsAddSignTarget_ChainEdgesNotTraversed()
    {
        // M1c：讀者自己是加簽人，主節點到加簽的串線也要歸零；主節點（別人簽的）是 hidden
        var (r, id) = await Boot(ForkJoin());
        await MustAddSign(id, "fin1", "emp1");
        await RevokeRead(r);

        var view = await ViewAs(id, "emp1");

        var small = view.Graph!.Nodes.Single(n => n.Kind == ApprovalProgressKinds.AddSign);
        small.State.Should().Be(ApprovalProgressStates.Active);
        small.RunIds.Should().Equal(view.Stages.Single().Id);
        State(view, "a").Should().Be(ApprovalProgressStates.Hidden);
        view.Graph.Edges.Should().OnlyContain(e => !e.Traversed);
        view.Graph.Edges.Should().Contain(e => e.To == small.Id);
    }

    [Fact]
    public async Task Progress_OwnStageReader_OwnNodeRerunByOthers_IsChanged_RunIdsOnlyOwn()
    {
        // fin1 簽過 a；gm 退回上一關後 a 重來，新的待簽被改派給別人（直接改資料）：fin1 只看得到第一段，節點顯示「已處理（之後有變動）」
        var (r, id) = await Boot(Line());
        await MustApprove(id, "fin1");
        await MustReject(id, "gm", RejectTaskHandler.ToPrevious);
        var firstRun = Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == id && x.NodeId == "a").OrderBy(x => x.Seq).First().Id;
        var rerun = Db.Context.Set<ApprovalTask>().Single(t => t.InstanceId == id && t.Status == ApprovalTaskStatus.Open);
        rerun.AssigneeUserId = rerun.OriginalUserId = "fin2";
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        await RevokeRead(r);

        var view = await ViewAs(id, "fin1");

        State(view, "a").Should().Be(ApprovalProgressStates.Changed);
        Node(view, "a").RunIds.Should().Equal(firstRun);
        view.Stages.Select(s => s.Id).Should().Equal(firstRun);
        State(view, "b").Should().Be(ApprovalProgressStates.Hidden);   // 不透露 b 被退回
        view.Graph!.Edges.Should().OnlyContain(e => !e.Traversed);
    }

    [Fact]
    public async Task Progress_OwnStageReader_OthersAddSign_NotDrawn()
    {
        // 加簽是執行中才產生的資料：別的關卡有沒有人加簽，只看自己關卡的讀者不該知道；主節點的出線改回從主節點出發
        var (r, id) = await Boot(ForkJoin());
        await MustAddSign(id, "fin1", "emp1");
        await RevokeRead(r);

        var view = await ViewAs(id, "buyer2");

        view.Graph!.Nodes.Select(n => n.Id).Should().Equal("start", "f", "a", "b", "j", "c", "end");
        var edge = view.Graph.Edges.Single(e => e.Id == "aj");
        edge.From.Should().Be("a");
        view.Graph.Edges.Select(e => e.Id).Should().Equal("e0", "fa", "fb", "aj", "bj", "jc", "ce");
        JsonSerializer.Serialize(view.Graph, Web).Should().NotContain("as-").And.NotContain("addSign");
        State(view, "b").Should().Be(ApprovalProgressStates.Active);
    }

    [Fact]
    public async Task Progress_OwnStageReader_OwnAddSignKept_OthersContractedAway()
    {
        // a 上先後兩個加簽小節點（fin1 → emp1，emp1 再加簽 emp2）：emp2 只留自己的那個，前一個拿掉後線改從主節點出發
        var (r, id) = await Boot(ForkJoin());
        await MustAddSign(id, "fin1", "emp1");
        await MustAddSign(id, "emp1", "emp2");
        await RevokeRead(r);

        var view = await ViewAs(id, "emp2");

        var smalls = view.Graph!.Nodes.Where(n => n.Kind == ApprovalProgressKinds.AddSign).ToList();
        smalls.Should().ContainSingle().Which.State.Should().Be(ApprovalProgressStates.Active);
        view.Graph.Edges.Single(e => e.To == smalls[0].Id).From.Should().Be("a");
        view.Graph.Edges.Single(e => e.Id == "aj").From.Should().Be(smalls[0].Id);
        view.Graph.Edges.Should().OnlyContain(e => !e.Traversed);
        view.Graph.Edges.Select(e => e.From).Concat(view.Graph.Edges.Select(e => e.To)).Should().OnlyContain(n => view.Graph.Nodes.Any(x => x.Id == n));
    }

    [Fact]
    public async Task Progress_OwnStageReader_ContentChangedAction_NoStageRunId()
    {
        // 臨時讀取權看得到進行中實例的 content-changed，它掛在別人的關卡：不帶出那一列的代號
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 150000);
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        (await Send(new CreateRecordRequest(r.Extra, V((TitleId(r.Extra), "追加"), (r.ExtraRef, po.Id), (r.ExtraAmount, 1000))))).IsCreated.Should().BeTrue();
        await MustApprove(id, "gm");
        await Db.Context.Set<UserGroupMapping>().Where(m => m.UserId == "buyer2"
            && m.GroupId == Db.Context.Set<PermissionGroup>().Single(g => g.Key == "everyone").Id).ExecuteDeleteAsync();

        var view = await ViewAs(id, "buyer2");

        var own = view.Stages.Select(s => s.Id).ToHashSet();
        var changed = view.Actions.Single(a => a.Action == ApprovalActions.ContentChanged);
        changed.StageRunId.Should().BeNull();
        view.Actions.Should().NotContain(a => a.StageRunId != null && !own.Contains(a.StageRunId.Value));
        var stored = ActionsOf(id).Single(a => a.Action == ApprovalActions.ContentChanged).StageRunId;
        stored.Should().NotBeNull();   // 資料庫裡有掛在某一關（別人的），遮蔽才是 null 的原因
        own.Should().NotContain(stored!.Value);
    }

    [Fact]
    public async Task Progress_Initiator_And_Manager_SeeAllStates()
    {
        var (r, id) = await Boot(ForkJoin());
        await MustApprove(id, "fin1");
        await RevokeRead(r);

        foreach (var reader in new[] { "buyer1", "admin" })
        {
            var view = await ViewAs(id, reader);
            State(view, "a").Should().Be(ApprovalProgressStates.Passed, reader);
            State(view, "b").Should().Be(ApprovalProgressStates.Active, reader);
            State(view, "j").Should().Be(ApprovalProgressStates.Active, reader);   // a 已到達，等 b
            Traversed(view).Should().BeEquivalentTo("e0", "fa", "fb", "aj");
        }
    }

    // ───────── 先前的簽核、快照遺失 ─────────

    [Fact]
    public async Task Progress_PreviousInstances_EachHasOwnGraph()
    {
        var (r, id) = await Boot(ForkJoin());
        var recordId = Inst(id).RecordId;
        await MustReject(id, "fin1", RejectTaskHandler.ToInitiator);   // 退回發起人，再送出
        As("buyer1");
        var again = (await MustSubmit(recordId, r.Confirmed)).InstanceId!.Value;

        var all = await RecordView(recordId, "buyer1");

        all.Current!.Id.Should().Be(again);
        State(all.Current, "a").Should().Be(ApprovalProgressStates.Active);   // 新實例重新從頭
        var old = all.Previous.Single();
        old.Id.Should().Be(id);
        State(old, "a").Should().Be(ApprovalProgressStates.Returned);
        State(old, "b").Should().Be(ApprovalProgressStates.Ended);
        State(old, "j").Should().Be(ApprovalProgressStates.NotReached);
    }

    [Fact]
    public async Task Progress_DefinitionMissing_GraphNull()
    {
        var (_, id) = await Boot(ForkJoin());
        await UpdateInstance(id, inst => inst.FlowSetSnapshot = []);

        var view = await ViewAs(id, "buyer1");

        view.Graph.Should().BeNull();
        view.Stages.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Progress_StructurallyInvalidButConsistent_StillReturnsGraph()
    {
        // 條件節點的預設線被清掉（結構再驗不過、引擎不會再跑），但節點代號都在：顯示不是執行，照樣回圖
        var (r, id) = await Boot(WithCondition);
        var broken = G(
            [Start(), Condition("c"), Approval("a", Users("fin1")), End()],
            [E("e0", "start", "c"), E("ca", "c", "a", Cond(r.Total, "gte", 1), order: 1), E("cd", "c", "end", Cond(r.Total, "gte", 2), order: 2), E("ae", "a", "end")]);
        await SetDefinition(id, broken);

        var view = await ViewAs(id, "buyer1");

        State(view, "c").Should().Be(ApprovalProgressStates.Passed);
        State(view, "a").Should().Be(ApprovalProgressStates.Active);
        Traversed(view).Should().Equal("e0", "ca");
    }

    // ───────── 壞資料（M8）：圖是 null，時間軸與歷程照常 ─────────

    private async Task AssertGraphNullButTimelineIntact(long id, ApprovalDefinition broken)
    {
        var before = await ViewAs(id, "buyer1");
        await SetDefinition(id, broken);

        var view = await ViewAs(id, "buyer1");

        view.Graph.Should().BeNull();
        view.Stages.Select(s => (s.Id, s.Status)).Should().Equal(before.Stages.Select(s => (s.Id, s.Status)));
        view.Actions.Select(a => a.Action).Should().Equal(before.Actions.Select(a => a.Action));
        view.Stages.Should().NotBeEmpty();
        view.Actions.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Progress_DuplicateNodeIds_GraphNull_StagesAndActionsIntact()
    {
        var (_, id) = await Boot(Line());

        await AssertGraphNullButTimelineIntact(id, G(
            [Start(), Approval("a", Users("fin1")), Approval("a", Users("gm")), End()],
            [E("e1", "start", "a"), E("e2", "a", "end")]));
    }

    [Fact]
    public async Task Progress_DanglingEdge_GraphNull_StagesAndActionsIntact()
    {
        var (_, id) = await Boot(Line());

        await AssertGraphNullButTimelineIntact(id, G(
            [Start(), Approval("a", Users("fin1")), Approval("b", Users("gm")), End()],
            [E("e1", "start", "a"), E("e2", "a", "b"), E("e3", "b", "end"), E("e4", "b", "nowhere")]));
    }

    [Fact]
    public async Task Progress_RunsReferToMissingNode_GraphNull_StagesAndActionsIntact()
    {
        var (r, id) = await Boot(ForkJoin());

        await AssertGraphNullButTimelineIntact(id, G(
            [Start(), Condition("x"), Approval("y", Users("fin1")), End()],
            [E("e0", "start", "x"), E("xa", "x", "y", Cond(r.Total, "gte", 1), order: 1), E("xd", "x", "end", isDefault: true), E("ye", "y", "end")]));
    }

    // ───────── 歷程的 stageRunId（J6） ─────────

    [Fact]
    public async Task ActionView_HasStageRunId_ForStageActions()
    {
        var (_, id) = await Boot(Line());
        await MustApprove(id, "fin1");

        var view = await ViewAs(id, "buyer1");

        var runOfA = view.Stages.Single(s => s.NodeId == "a").Id;
        view.Actions.Single(a => a.Action == ApprovalActions.Approve).StageRunId.Should().Be(runOfA);
        JsonSerializer.Serialize(view.Actions.Single(a => a.Action == ApprovalActions.Approve), Web).Should().Contain($"\"stageRunId\":{runOfA}");
    }

    [Fact]
    public async Task ActionView_Submit_NoStageRunId()
    {
        var (_, id) = await Boot(Line());

        var submit = (await ViewAs(id, "buyer1")).Actions.Single(a => a.Action == ApprovalActions.Submit);

        submit.StageRunId.Should().BeNull();
        JsonSerializer.Serialize(submit, Web).Should().NotContain("stageRunId");
    }
}
