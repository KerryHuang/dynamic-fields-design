using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>進度圖的節點狀態、加簽小節點與走過的線（計畫 fg4 Task 3；J4、J10、J15、J16、U5）：手造節點實例，不碰資料庫。</summary>
public class ApprovalGraphProgressTests
{
    private const string Ok = ApprovalStageStatus.Approved, Skip = ApprovalStageStatus.Skipped, Ret = ApprovalStageStatus.Returned,
        Act = ApprovalStageStatus.Active, Closed = ApprovalStageStatus.Closed, Passed = ApprovalStageStatus.Passed, Waiting = ApprovalStageStatus.Waiting,
        Queued = ApprovalStageStatus.Queued;

    // ───────── 定義 ─────────

    private static I18nText Name(string id) => new([KeyValuePair.Create("zh-TW", id)]);

    private static ApprovalNodeDef N(string id, string kind, string? mode = null, string? forkId = null) =>
        kind == ApprovalNodeKinds.Approval
            ? new(id, kind, Name(id), Approvers: new ApproverSource(ApprovalSources.Users, UserIds: ["u"]), Mode: mode ?? ApprovalModes.Any, EditableFields: [], OnEmpty: ApprovalOnEmpty.Fallback)
            : new(id, kind, Name(id), Mode: mode, ForkId: forkId);

    private static ApprovalNodeDef A(string id) => N(id, ApprovalNodeKinds.Approval);
    private static ApprovalNodeDef Start() => N("start", ApprovalNodeKinds.Start);
    private static ApprovalNodeDef End() => N("end", ApprovalNodeKinds.End);
    private static ApprovalNodeDef Fork(string id) => N(id, ApprovalNodeKinds.Fork);
    private static ApprovalNodeDef Join(string id, string fork, string mode = ApprovalModes.All) => N(id, ApprovalNodeKinds.Join, mode, fork);
    private static ApprovalEdgeDef E(string id, string from, string to) => new(id, from, to);
    private static ApprovalDefinition Def(List<ApprovalNodeDef> nodes, List<ApprovalEdgeDef> edges) =>
        new(ApprovalShapes.Graph, null, null, null, [], nodes, edges);

    /// <summary>start → a → b → end（e1、e2、e3）。</summary>
    private static ApprovalDefinition Line() => Def([Start(), A("a"), A("b"), End()], [E("e1", "start", "a"), E("e2", "a", "b"), E("e3", "b", "end")]);

    /// <summary>start → fork f → 分支 a、b → join j → c → end。</summary>
    private static ApprovalDefinition ForkJoin(string joinMode = ApprovalModes.All) => Def(
        [Start(), Fork("f"), A("a"), A("b"), Join("j", "f", joinMode), A("c"), End()],
        [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("jc", "j", "c"), E("ce", "c", "end")]);

    /// <summary>分支 fa 是 a；分支 fb 是 b1 → b2；任一到齊。</summary>
    private static ApprovalDefinition AnyWithSerialBranch() => Def(
        [Start(), Fork("f"), A("a"), A("b1"), A("b2"), Join("j", "f", ApprovalModes.Any), End()],
        [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b1"), E("b12", "b1", "b2"), E("aj", "a", "j"), E("b2j", "b2", "j"), E("je", "j", "end")]);

    // ───────── 節點實例 ─────────

    /// <summary>graph 的節點實例：Id 與 Seq 相同，StageCode = NodeId。</summary>
    private static ApprovalStageRun G(int seq, string node, string kind, string status, string? inEdge = null, string branch = "", string? reason = null,
        bool activated = true) =>
        new()
        {
            Id = seq, Seq = seq, StageCode = node, NodeId = node, NodeKind = kind, BranchKey = branch, Status = status, InEdgeId = inEdge, CloseReason = reason,
            ActivatedTime = activated ? new DateTime(2026, 11, 1) : null
        };

    private static ApprovalStageRun GA(int seq, string host, string status, string by = "m", string[]? to = null, bool activated = true, string branch = "") =>
        new()
        {
            Id = seq, Seq = seq, StageCode = null, NodeId = host, NodeKind = ApprovalNodeKinds.Approval, BranchKey = branch, Status = status,
            AddedByUserId = by, AddSignUserIds = [.. to ?? ["x"]], Mode = ApprovalModes.Any, ActivatedTime = activated ? new DateTime(2026, 11, 1) : null
        };

    private static ApprovalStageRun Gs(int seq, string node, string status, string? inEdge = null, string branch = "", string? reason = null) =>
        G(seq, node, ApprovalNodeKinds.Approval, status, inEdge, branch, reason);

    private static ApprovalStageRun Pass(int seq, string node, string kind, string? inEdge = null, string branch = "") => G(seq, node, kind, Passed, inEdge, branch);

    /// <summary>sequence 的節點實例：NodeId、NodeKind、BranchKey 都是 null。</summary>
    private static ApprovalStageRun S(int seq, string? code, string status, bool activated = true) =>
        new() { Id = seq, Seq = seq, StageCode = code, Status = status, ActivatedTime = activated ? new DateTime(2026, 11, 1) : null };

    private static ApprovalStageRun SA(int seq, string status, bool activated = true, string by = "m", string[]? to = null) =>
        new()
        {
            Id = seq, Seq = seq, StageCode = null, Status = status, AddedByUserId = by, AddSignUserIds = [.. to ?? ["x"]], Mode = ApprovalModes.Any,
            ActivatedTime = activated ? new DateTime(2026, 11, 1) : null
        };

    // ───────── 輔助 ─────────

    private static readonly IReadOnlySet<long> NoRejected = new HashSet<long>();

    private static ApprovalProgress Of(ApprovalDefinition d, IEnumerable<ApprovalStageRun> runs, IEnumerable<long>? rejected = null, bool byCode = false, bool approved = false)
    {
        var p = ApprovalGraphProgress.Of(d, [.. runs], rejected is null ? NoRejected : rejected.ToHashSet(), byCode, approved);
        p.Should().NotBeNull();
        return p!;
    }

    private static string State(ApprovalProgress p, string id) => p.Nodes.Single(n => n.Id == id).State;
    private static string[] Traversed(ApprovalProgress p) => [.. p.Edges.Where(e => e.Traversed).Select(e => e.Id)];

    private static ApprovalDefinition SeqDef(params string[] codes) => ApprovalGraphConversion.FromSequence(new ApprovalDefinition(
        ApprovalShapes.Sequence, null, null, null,
        [.. codes.Select(c => new ApprovalStageDef(c, Name(c), null, new ApproverSource(ApprovalSources.Users, UserIds: ["u"]), ApprovalModes.Any, [], null, ApprovalOnEmpty.Fallback))]));

    // ───────── graph ─────────

    [Fact]
    public void Graph_ApprovedAndPassed_ArePassed_TraversedFromInEdgeId()
    {
        var p = Of(Line(), [Pass(1, "start", ApprovalNodeKinds.Start), Gs(2, "a", Ok, "e1"), Gs(3, "b", Act, "e2")]);

        State(p, "start").Should().Be(ApprovalProgressStates.Passed);
        State(p, "a").Should().Be(ApprovalProgressStates.Passed);
        State(p, "b").Should().Be(ApprovalProgressStates.Active);
        State(p, "end").Should().Be(ApprovalProgressStates.NotReached);
        Traversed(p).Should().Equal("e1", "e2");
        p.Nodes.Single(n => n.Id == "a").RunIds.Should().Equal(2);
        p.Nodes.Single(n => n.Id == "start").RunIds.Should().BeEmpty();
    }

    [Fact]
    public void Graph_Active_And_JoinWaiting_AreActive()
    {
        var p = Of(ForkJoin(), [
            Pass(1, "start", ApprovalNodeKinds.Start), Pass(2, "f", ApprovalNodeKinds.Fork, "e0"),
            Gs(3, "a", Act, "fa", "fa"), Gs(4, "b", Ok, "fb", "fb"), G(5, "j", ApprovalNodeKinds.Join, Waiting, "bj")]);

        State(p, "a").Should().Be(ApprovalProgressStates.Active);
        State(p, "b").Should().Be(ApprovalProgressStates.Passed);
        State(p, "j").Should().Be(ApprovalProgressStates.Active);
        State(p, "c").Should().Be(ApprovalProgressStates.NotReached);
        p.Nodes.Single(n => n.Id == "j").JoinMode.Should().Be(ApprovalModes.All);
        Traversed(p).Should().Equal("e0", "fa", "fb", "bj");
    }

    [Fact]
    public void Graph_Join_PassedIfAnyArrivalPassed()
    {
        // J30：匯合的到達列一條分支一列；只取 Seq 最大的一列會拿到另一條分支較晚寫入的 waiting
        var p = Of(ForkJoin(ApprovalModes.Any), [
            Gs(3, "a", Ok, "fa", "fa"), G(4, "j", ApprovalNodeKinds.Join, Passed, "aj"), G(6, "j", ApprovalNodeKinds.Join, Waiting, "bj")]);

        State(p, "j").Should().Be(ApprovalProgressStates.Passed);
    }

    [Fact]
    public void Graph_AnyJoin_CancelledBranch_IsCancelled()
    {
        var p = Of(ForkJoin(ApprovalModes.Any), [
            Pass(1, "start", ApprovalNodeKinds.Start), Pass(2, "f", ApprovalNodeKinds.Fork, "e0"),
            Gs(3, "a", Ok, "fa", "fa"), Gs(4, "b", Closed, "fb", "fb", ApprovalCloseReasons.BranchCancelled), G(5, "j", ApprovalNodeKinds.Join, Passed, "aj"),
            Gs(6, "c", Act, "jc")]);

        State(p, "b").Should().Be(ApprovalProgressStates.Cancelled);
        State(p, "j").Should().Be(ApprovalProgressStates.Passed);
        State(p, "c").Should().Be(ApprovalProgressStates.Active);
    }

    [Fact]
    public void Graph_CancelledBranch_UnreachedNodesAreCancelled()
    {
        // U5：被取消分支段裡沒有節點實例的 b2 不能看起來像還會輪到它
        var p = Of(AnyWithSerialBranch(), [
            Pass(1, "start", ApprovalNodeKinds.Start), Pass(2, "f", ApprovalNodeKinds.Fork, "e0"),
            Gs(3, "a", Ok, "fa", "fa"), Gs(4, "b1", Closed, "fb", "fb", ApprovalCloseReasons.BranchCancelled), G(5, "j", ApprovalNodeKinds.Join, Passed, "aj")]);

        State(p, "b1").Should().Be(ApprovalProgressStates.Cancelled);
        State(p, "b2").Should().Be(ApprovalProgressStates.Cancelled);
        State(p, "end").Should().Be(ApprovalProgressStates.NotReached);
        p.Nodes.Single(n => n.Id == "b2").RunIds.Should().BeEmpty();
    }

    [Fact]
    public void Graph_CancelledBranch_NestedForkInside_AllUnreachedCancelled()
    {
        // 分支 fb 裡還有一組並行 f2 → x、y → j2：被取消後 f2 以外的節點都沒有被走完
        var d = Def(
            [Start(), Fork("f"), A("a"), Fork("f2"), A("x"), A("y"), Join("j2", "f2"), Join("j", "f", ApprovalModes.Any), A("z"), End()],
            [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "f2"), E("f2x", "f2", "x"), E("f2y", "f2", "y"), E("xj2", "x", "j2"), E("yj2", "y", "j2"),
             E("j2j", "j2", "j"), E("aj", "a", "j"), E("jz", "j", "z"), E("ze", "z", "end")]);

        var p = Of(d, [
            Pass(1, "start", ApprovalNodeKinds.Start), Pass(2, "f", ApprovalNodeKinds.Fork, "e0"),
            Gs(3, "a", Ok, "fa", "fa"), Pass(4, "f2", ApprovalNodeKinds.Fork, "fb", "fb"),
            Gs(5, "x", Closed, "f2x", "fb/f2x", ApprovalCloseReasons.BranchCancelled), Gs(6, "y", Closed, "f2y", "fb/f2y", ApprovalCloseReasons.BranchCancelled),
            G(7, "j", ApprovalNodeKinds.Join, Passed, "aj")]);

        State(p, "f2").Should().Be(ApprovalProgressStates.Passed);
        State(p, "x").Should().Be(ApprovalProgressStates.Cancelled);
        State(p, "y").Should().Be(ApprovalProgressStates.Cancelled);
        State(p, "j2").Should().Be(ApprovalProgressStates.Cancelled);
        State(p, "z").Should().Be(ApprovalProgressStates.NotReached);
    }

    [Fact]
    public void Graph_InstanceEnded_IsEnded()
    {
        var p = Of(ForkJoin(), [
            Pass(1, "start", ApprovalNodeKinds.Start), Pass(2, "f", ApprovalNodeKinds.Fork, "e0"),
            Gs(3, "a", Closed, "fa", "fa", ApprovalCloseReasons.InstanceEnded), G(4, "j", ApprovalNodeKinds.Join, Closed, "bj")]);

        State(p, "a").Should().Be(ApprovalProgressStates.Ended);
        State(p, "j").Should().Be(ApprovalProgressStates.Ended);
        State(p, "c").Should().Be(ApprovalProgressStates.NotReached);
    }

    [Fact]
    public void Graph_RejectToInitiator_ClosedRunWithRejectedTask_IsReturned()
    {
        // 退回發起人：被退回的那一列是 closed ＋ instanceEnded，要靠「有被退回任務」認出來；另一條分支的列只是被一併關閉
        var p = Of(ForkJoin(), [
            Pass(1, "start", ApprovalNodeKinds.Start), Pass(2, "f", ApprovalNodeKinds.Fork, "e0"),
            Gs(3, "a", Closed, "fa", "fa", ApprovalCloseReasons.InstanceEnded), Gs(4, "b", Closed, "fb", "fb", ApprovalCloseReasons.InstanceEnded)], rejected: [3]);

        State(p, "a").Should().Be(ApprovalProgressStates.Returned);
        State(p, "b").Should().Be(ApprovalProgressStates.Ended);
    }

    [Fact]
    public void Graph_ReturnPrevious_LatestRunWins_AllRunIdsListed()
    {
        // a 同意、b 退回上一關 → a 重來（Seq 3）：a 以最新一次上色，runIds 全列
        var p = Of(Line(), [Gs(1, "a", Ok, "e1"), Gs(2, "b", Ret, "e2"), Gs(3, "a", Act, "e1")]);

        State(p, "a").Should().Be(ApprovalProgressStates.Active);
        p.Nodes.Single(n => n.Id == "a").RunIds.Should().Equal(1, 3);
        State(p, "b").Should().Be(ApprovalProgressStates.Returned);

        var again = Of(Line(), [Gs(1, "a", Ok, "e1"), Gs(2, "b", Ret, "e2"), Gs(3, "a", Ok, "e1"), Gs(4, "b", Act, "e2")]);
        State(again, "b").Should().Be(ApprovalProgressStates.Active);
        again.Nodes.Single(n => n.Id == "b").RunIds.Should().Equal(2, 4);
    }

    [Fact]
    public void Graph_Skipped_IsSkipped_EdgesThroughItTraversed()
    {
        var p = Of(Line(), [Gs(1, "a", Skip, "e1"), Gs(2, "b", Act, "e2")]);

        State(p, "a").Should().Be(ApprovalProgressStates.Skipped);
        Traversed(p).Should().Equal("e1", "e2");
    }

    [Fact]
    public void Graph_NoRun_IsNotReached()
    {
        var p = Of(Line(), []);

        State(p, "start").Should().Be(ApprovalProgressStates.Passed);   // 實例存在，開始節點一定走過
        State(p, "a").Should().Be(ApprovalProgressStates.NotReached);
        State(p, "end").Should().Be(ApprovalProgressStates.NotReached);
        Traversed(p).Should().BeEmpty();
    }

    [Fact]
    public void Graph_UnsupportedKind_StillProducesNodes()
    {
        // 通知節點（fg5 之前引擎不支援）：進度圖照畫，有節點實例就依實例上色
        var d = Def([Start(), N("n", ApprovalNodeKinds.Notify), End()], [E("e1", "start", "n"), E("e2", "n", "end")]);

        State(Of(d, []), "n").Should().Be(ApprovalProgressStates.NotReached);
        var p = Of(d, [Pass(1, "start", ApprovalNodeKinds.Start), Pass(2, "n", ApprovalNodeKinds.Notify, "e1")]);
        State(p, "n").Should().Be(ApprovalProgressStates.Passed);
        p.Nodes.Single(n => n.Id == "n").Kind.Should().Be(ApprovalNodeKinds.Notify);
    }

    [Fact]
    public void Graph_StructurallyInvalidButConsistent_ProducesGraph_WithoutCancelledEnrichment()
    {
        // 多一個走不到的節點 z：結構再驗不過（顯示不是執行，照樣回圖），但不補「被取消分支段」的標記
        var d = AnyWithSerialBranch() with { Nodes = [.. AnyWithSerialBranch().Nodes!, A("z")] };
        var p = Of(d, [
            Pass(1, "start", ApprovalNodeKinds.Start), Pass(2, "f", ApprovalNodeKinds.Fork, "e0"),
            Gs(3, "a", Ok, "fa", "fa"), Gs(4, "b1", Closed, "fb", "fb", ApprovalCloseReasons.BranchCancelled), G(5, "j", ApprovalNodeKinds.Join, Passed, "aj")]);

        State(p, "b1").Should().Be(ApprovalProgressStates.Cancelled);   // 有節點實例的照實例
        State(p, "b2").Should().Be(ApprovalProgressStates.NotReached);   // 沒有補標
        State(p, "z").Should().Be(ApprovalProgressStates.NotReached);
    }

    // ───────── 加簽 ─────────

    [Fact]
    public void AddSign_GraphAttachedByNodeId_InsertedAfterHost_ChainEdgesFromBackend()
    {
        var p = Of(Line(), [Gs(1, "a", Ok, "e1"), GA(2, "a", Act)]);

        p.Nodes.Select(n => n.Id).Should().Equal("start", "a", "as-2", "b", "end");
        var small = p.Nodes.Single(n => n.Id == "as-2");
        small.Kind.Should().Be(ApprovalProgressKinds.AddSign);
        small.HostId.Should().Be("a");
        small.State.Should().Be(ApprovalProgressStates.Active);
        small.Name.Should().BeNull();
        small.RunIds.Should().Equal(2);
        // 主節點原本的出線 e2 改從小節點出發；串線放在它的位置
        p.Edges.Select(e => (e.Id, e.From, e.To)).Should().Equal(
            ("e1", "start", "a"), ("as-2-in", "a", "as-2"), ("e2", "as-2", "b"), ("e3", "b", "end"));
        p.Edges.Single(e => e.Id == "as-2-in").Traversed.Should().BeTrue();
        p.Edges.Single(e => e.Id == "e2").Traversed.Should().BeFalse();
    }

    [Fact]
    public void AddSign_ReturnedAndRequeued_SameSmallNode_RunIdsInSeqOrder()
    {
        // 加簽被退回（Seq 2）→ 主節點重來（3）→ 另排的 queued 複本（4）：同一個加簽人、對象、方式，只有一個小節點
        var p = Of(Line(), [Gs(1, "a", Ok, "e1"), GA(2, "a", Ret), Gs(3, "a", Act, "e1"), GA(4, "a", Queued, activated: false)]);

        var smalls = p.Nodes.Where(n => n.Kind == ApprovalProgressKinds.AddSign).ToList();
        smalls.Should().ContainSingle();
        smalls[0].Id.Should().Be("as-2");
        smalls[0].RunIds.Should().Equal(2, 4);
        smalls[0].State.Should().Be(ApprovalProgressStates.NotReached);   // 最新一列是排隊中
        p.Edges.Single(e => e.Id == "as-2-in").Traversed.Should().BeTrue();   // 第一列啟動過
    }

    [Fact]
    public void AddSign_ClosedBeforeStart_NotDrawn()
    {
        var p = Of(Line(), [Gs(1, "a", Ok, "e1"), GA(2, "a", Closed, activated: false), Gs(3, "b", Act, "e2")]);

        p.Nodes.Should().NotContain(n => n.Kind == ApprovalProgressKinds.AddSign);
        p.Edges.Select(e => (e.Id, e.From)).Should().Equal(("e1", "start"), ("e2", "a"), ("e3", "b"));
    }

    [Fact]
    public void AddSign_Queued_IsNotReached_ChainNotTraversed()
    {
        var p = Of(Line(), [Gs(1, "a", Act, "e1"), GA(2, "a", Queued, activated: false)]);

        var small = p.Nodes.Single(n => n.Kind == ApprovalProgressKinds.AddSign);
        small.State.Should().Be(ApprovalProgressStates.NotReached);
        small.RunIds.Should().Equal(2);
        p.Edges.Single(e => e.Id == "as-2-in").Traversed.Should().BeFalse();
    }

    [Fact]
    public void AddSign_TwoDistinctOnSameHost_ChainedBySeq()
    {
        var p = Of(Line(), [Gs(1, "a", Ok, "e1"), GA(2, "a", Ok, to: ["x"]), GA(3, "a", Act, by: "n", to: ["y"])]);

        p.Nodes.Select(n => n.Id).Should().Equal("start", "a", "as-2", "as-3", "b", "end");
        p.Edges.Select(e => (e.Id, e.From, e.To)).Should().Equal(
            ("e1", "start", "a"), ("as-2-in", "a", "as-2"), ("as-3-in", "as-2", "as-3"), ("e2", "as-3", "b"), ("e3", "b", "end"));
    }

    // ───────── sequence 與備援 ─────────

    [Fact]
    public void Sequence_RunsMapByStageCode_TraversedDerived()
    {
        var p = Of(SeqDef("a", "b", "c"), [S(1, "a", Ok), S(2, "b", Act)], byCode: true);

        State(p, "start").Should().Be(ApprovalProgressStates.Passed);
        State(p, "a").Should().Be(ApprovalProgressStates.Passed);
        State(p, "b").Should().Be(ApprovalProgressStates.Active);
        State(p, "c").Should().Be(ApprovalProgressStates.NotReached);
        State(p, "end").Should().Be(ApprovalProgressStates.NotReached);
        Traversed(p).Should().Equal("e1", "e2");
        p.Nodes.Single(n => n.Id == "b").RunIds.Should().Equal(2);
    }

    [Fact]
    public void Sequence_EndPassedOnlyWhenApproved()
    {
        var runs = new[] { S(1, "a", Ok), S(2, "b", Ok) };

        var open = Of(SeqDef("a", "b"), runs, byCode: true);
        State(open, "end").Should().Be(ApprovalProgressStates.NotReached);
        Traversed(open).Should().Equal("e1", "e2");

        var done = Of(SeqDef("a", "b"), runs, byCode: true, approved: true);
        State(done, "end").Should().Be(ApprovalProgressStates.Passed);
        Traversed(done).Should().Equal("e1", "e2", "e3");
    }

    [Fact]
    public void Sequence_RejectToInitiator_IsReturned()
    {
        var p = Of(SeqDef("a", "b"), [S(1, "a", Ok), S(2, "b", Closed)], rejected: [2], byCode: true);

        State(p, "b").Should().Be(ApprovalProgressStates.Returned);
    }

    [Fact]
    public void Sequence_Withdrawn_ActiveStageIsEnded()
    {
        var p = Of(SeqDef("a", "b"), [S(1, "a", Ok), S(2, "b", Closed)], byCode: true);

        State(p, "b").Should().Be(ApprovalProgressStates.Ended);
    }

    [Fact]
    public void Sequence_AddSign_AttachedToPrecedingStage()
    {
        var p = Of(SeqDef("a", "b"), [S(1, "a", Ok), SA(2, Act)], byCode: true);

        p.Nodes.Select(n => n.Id).Should().Equal("start", "a", "as-2", "b", "end");
        p.Nodes.Single(n => n.Id == "as-2").HostId.Should().Be("a");
    }

    [Fact]
    public void Sequence_ReturnPrevious_RequeuedAddSign_AttachedToRestartedStage()
    {
        // a(1) 同意、加簽 X(2) 被退回上一關 → a 重來(3)、X 排隊複本(4)：兩列同一個小節點，掛在 a
        var p = Of(SeqDef("a", "b"), [S(1, "a", Ok), SA(2, Ret), S(3, "a", Act), SA(4, Queued, activated: false)], byCode: true);

        var small = p.Nodes.Single(n => n.Kind == ApprovalProgressKinds.AddSign);
        small.HostId.Should().Be("a");
        small.RunIds.Should().Equal(2, 4);
    }

    [Fact]
    public void Sequence_ReturnPrevious_ToAddSign_RestartedAddSignAttachedToOriginalHost()
    {
        // 審查 M1a 的反例：a(1) 同意並加簽 X(2)、X 同意、b(3) 退回上一關 → 重建 X''(4)。
        // 4 之前最近的有代號列是 b(3，returned)，要跳過它，X'' 才會和 X 一樣掛在 a
        var p = Of(SeqDef("a", "b"), [S(1, "a", Ok), SA(2, Ok), S(3, "b", Ret), SA(4, Act)], byCode: true);

        var small = p.Nodes.Single(n => n.Kind == ApprovalProgressKinds.AddSign);
        small.HostId.Should().Be("a");
        small.RunIds.Should().Equal(2, 4);
        small.State.Should().Be(ApprovalProgressStates.Active);
        State(p, "b").Should().Be(ApprovalProgressStates.Returned);
        p.Nodes.Select(n => n.Id).Should().Equal("start", "a", "as-2", "b", "end");
    }

    [Fact]
    public void Sequence_StageCodeStart_UsesConvertedStart0()
    {
        // 關卡代號撞到 start：FromSequence 把開始節點改成 start-0，代號 start 的關卡仍對得到自己的節點
        var p = Of(SeqDef("start", "b"), [S(1, "start", Act)], byCode: true);

        State(p, "start-0").Should().Be(ApprovalProgressStates.Passed);
        State(p, "start").Should().Be(ApprovalProgressStates.Active);
        p.Nodes.Single(n => n.Id == "start").Kind.Should().Be(ApprovalNodeKinds.Approval);
    }

    [Fact]
    public void Fallback_ThreeNodes_AddSignOnFallback()
    {
        var d = Def([Start(), A("_fallback"), End()], [E("e1", "start", "_fallback"), E("e2", "_fallback", "end")]);

        var p = Of(d, [S(1, "_fallback", Ok), SA(2, Act)], byCode: true);

        p.Nodes.Select(n => n.Id).Should().Equal("start", "_fallback", "as-2", "end");
        p.Nodes.Single(n => n.Id == "as-2").HostId.Should().Be("_fallback");
        State(p, "_fallback").Should().Be(ApprovalProgressStates.Passed);
        p.Edges.Select(e => (e.Id, e.From)).Should().Equal(("e1", "start"), ("as-2-in", "_fallback"), ("e2", "as-2"));
    }

    // ───────── 壞資料（M8）：回 null、不丟例外 ─────────

    private static ApprovalProgress? Raw(ApprovalDefinition d, IEnumerable<ApprovalStageRun> runs, bool byCode = false) =>
        ApprovalGraphProgress.Of(d, [.. runs], NoRejected, byCode, false);

    [Fact]
    public void Of_DuplicateNodeIds_ReturnsNull()
    {
        var d = Line() with { Nodes = [.. Line().Nodes!, A("a")] };

        Raw(d, []).Should().BeNull();
    }

    [Fact]
    public void Of_EdgeEndpointMissing_ReturnsNull()
    {
        var d = Line() with { Edges = [.. Line().Edges!, E("e9", "b", "nowhere")] };

        Raw(d, []).Should().BeNull();
    }

    [Fact]
    public void Of_RunNodeIdNotInDefinition_ReturnsNull()
    {
        Raw(Line(), [Gs(1, "ghost", Act)]).Should().BeNull();
        Raw(Line(), [GA(1, "ghost", Act)]).Should().BeNull();
        Raw(Line(), [Gs(1, "a", Act, "ghost-edge")]).Should().BeNull();
        Raw(SeqDef("a"), [S(1, "ghost", Act)], byCode: true).Should().BeNull();
    }

    [Fact]
    public void Of_JoinArrivalInEdgeNotOfThatJoin_ReturnsNull()
    {
        // 匯合到達列的進線不是這個匯合的進線：算被取消的分支段時才會發現，也要回 null
        var d = ForkJoin(ApprovalModes.Any);

        Raw(d, [G(1, "j", ApprovalNodeKinds.Join, Passed, "e0")]).Should().BeNull();
    }

    [Fact]
    public void Of_SameInput_SameOutput()
    {
        var runs = new[] { Gs(1, "a", Ok, "e1"), GA(2, "a", Act), Gs(3, "b", Ret, "e2") };

        var first = Of(Line(), runs);
        var second = Of(Line(), runs.Reverse());   // 輸入順序不同、結果相同（內部依 Seq 排）

        second.Should().BeEquivalentTo(first, o => o.WithStrictOrdering());
    }
}
