using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>graph 的分支段路徑函式（計畫 fg2 J4、J12、J13；設計 §8.4、§8.5）：手造節點實例，不碰資料庫。</summary>
public class ApprovalGraphPathTests
{
    private const string Ok = ApprovalStageStatus.Approved, Skip = ApprovalStageStatus.Skipped, Ret = ApprovalStageStatus.Returned,
        Act = ApprovalStageStatus.Active, Closed = ApprovalStageStatus.Closed, Passed = ApprovalStageStatus.Passed, Waiting = ApprovalStageStatus.Waiting;

    private static ApprovalStageRun Run(int seq, string? nodeId, string branch, string status, string? kind = ApprovalNodeKinds.Approval, string? inEdge = null, bool addSign = false) =>
        new()
        {
            Id = seq, Seq = seq, StageCode = addSign ? null : nodeId, NodeId = nodeId, BranchKey = branch, Status = status, NodeKind = kind, InEdgeId = inEdge,
            AddedByUserId = addSign ? "mgr" : null, AddSignUserIds = addSign ? ["x"] : null
        };

    private static ApprovalStageRun Fork(int seq, string id, string branch) => Run(seq, id, branch, Passed, ApprovalNodeKinds.Fork);
    private static ApprovalStageRun JoinRun(int seq, string id, string outer, string status, string inEdge) => Run(seq, id, outer, status, ApprovalNodeKinds.Join, inEdge);

    // ---- Previous ----

    [Fact]
    public void Previous_SameBranch_LastApproved()
    {
        var runs = new[] { Run(1, "a", "e1", Ok), Run(2, "b", "e1", Ok), Run(3, "c", "e1", Act) };
        ApprovalGraphPath.Previous(runs, runs[2])!.Seq.Should().Be(2);
    }

    [Fact]
    public void Previous_FirstInBranch_Null()
    {
        var runs = new[] { Run(1, "m", "", Ok), Fork(2, "f", ""), Run(3, "a", "e1", Act) };
        ApprovalGraphPath.Previous(runs, runs[2]).Should().BeNull();
    }

    [Fact]
    public void Previous_FirstAfterJoin_Null()
    {
        var runs = new[]
        {
            Run(1, "m", "", Ok), Fork(2, "f", ""), Run(3, "a", "e1", Ok), Run(4, "b", "e2", Ok),
            JoinRun(5, "j", "", Passed, "e7"), Run(6, "z", "", Act)
        };
        ApprovalGraphPath.Previous(runs, runs[5]).Should().BeNull();
    }

    [Fact]
    public void Previous_OtherBranchApproved_NotCandidate()
    {
        var runs = new[] { Fork(1, "f", ""), Run(2, "a", "e1", Ok), Run(3, "b", "e2", Act) };
        ApprovalGraphPath.Previous(runs, runs[2]).Should().BeNull();
    }

    [Fact]
    public void Previous_SkippedNotCounted()
    {
        var runs = new[] { Run(1, "a", "", Ok), Run(2, "b", "", Skip), Run(3, "c", "", Act) };
        ApprovalGraphPath.Previous(runs, runs[2])!.Seq.Should().Be(1);
    }

    [Fact]
    public void Previous_PassedConditionInBetween_StillFindsApproved()
    {
        var runs = new[] { Run(1, "a", "", Ok), Run(2, "c", "", Passed, ApprovalNodeKinds.Condition, "e2"), Run(3, "b", "", Act) };
        ApprovalGraphPath.Previous(runs, runs[2])!.Seq.Should().Be(1);
    }

    public static IEnumerable<object[]> SequenceShapedRuns() =>
    [
        // 退回後重來
        [new[] { Run(1, "a", "", Ok), Run(2, "b", "", Ok), Run(3, "c", "", Ret), Run(4, "b", "", Ok), Run(5, "c", "", Act) }],
        // 加簽（被退回的加簽重新排隊）
        [new[] { Run(1, "s1", "", Ok), Run(2, null, "", Ok, addSign: true), Run(3, "s2", "", Ret), Run(4, null, "", Ok, addSign: true), Run(5, "s2", "", Act) }],
        // 略過與 closed 的排隊關卡
        [new[] { Run(1, "a", "", Ok), Run(2, "b", "", Skip), Run(3, "c", "", Closed), Run(4, "d", "", Ret), Run(5, "a", "", Act), Run(6, "b", "", ApprovalStageStatus.Queued) }],
        // 真正的 sequence 列：NodeKind、BranchKey 都是 null
        [new[] { NullShaped(1, "a", Ok), NullShaped(2, "b", Ok), NullShaped(3, "c", Ret), NullShaped(4, "b", Ok), NullShaped(5, "c", Act) }],
    ];

    private static ApprovalStageRun NullShaped(int seq, string code, string status) =>
        new() { Id = seq, Seq = seq, StageCode = code, Status = status };

    [Theory]
    [MemberData(nameof(SequenceShapedRuns))]
    public void Previous_AfterReturnCut_SameAsApprovalPath(ApprovalStageRun[] runs)
    {
        foreach (var current in runs)
        {
            long? actual = ApprovalGraphPath.Previous(runs, current)?.Id, expected = ApprovalPath.Previous(runs, current)?.Id;
            actual.Should().Be(expected, $"current = {current.Seq}");
        }
    }

    [Fact]
    public void Previous_AddSignInBranch_Counted()
    {
        var runs = new[] { Fork(1, "f", ""), Run(2, "a", "e1", Ok), Run(3, "a", "e1", Ok, addSign: true), Run(4, "b", "e1", Act) };
        ApprovalGraphPath.Previous(runs, runs[3])!.Seq.Should().Be(3);
    }

    [Fact]
    public void Segment_OnlyThatBranch_CutsReturned()
    {
        var runs = new[]
        {
            Run(1, "a", "e1", Ok), Run(2, "x", "e2", Ok), Run(3, "c", "e1", Passed, ApprovalNodeKinds.Condition), Run(4, "b", "e1", Ret),
            Run(5, "a", "e1", Act)
        };
        // 退回上一關的目標是 a（路徑上最近的 approved），截到它之前；e2 的列不在段內。
        ApprovalGraphPath.Segment(runs, "e1").Should().BeEmpty();
        ApprovalGraphPath.Segment(runs, "e2").Select(r => r.Seq).Should().Equal(2);
    }

    [Fact]
    public void Segment_IncludesPassedConditionAndFork()
    {
        var runs = new[] { Run(1, "a", "", Ok), Run(2, "c", "", Passed, ApprovalNodeKinds.Condition, "e2"), Fork(3, "f", ""), Run(4, "b", "", Act) };
        ApprovalGraphPath.Segment(runs, "").Select(r => r.Seq).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Previous_InnerSegment_BarrierPerSegment()
    {
        var runs = new[]
        {
            Fork(1, "f", ""), Run(2, "x", "e1", Ok), Fork(3, "f2", "e1"), Run(4, "y", "e1/e9", Ok), Run(5, "z", "e1/e10", Ok),
            JoinRun(6, "j2", "e1", Passed, "yj2"), JoinRun(7, "j2", "e1", Passed, "zj2"), Run(8, "w", "e1", Act), Run(9, "y2", "e1/e9", Act)
        };
        // 內層匯合之後的第一個：段 e1 的最後一列匯合是 7，之前的 x 不算。
        ApprovalGraphPath.Previous(runs, runs[7]).Should().BeNull();
        // 內層分支內：段 e1/e9 沒有分支或匯合列，找得到同段的 y。
        ApprovalGraphPath.Previous(runs, runs[8])!.Seq.Should().Be(4);
    }

    // ---- CancelledRoots、ArrivedArms ----

    private static ApprovalNodeDef Appr(string id) => new(id, ApprovalNodeKinds.Approval, Approvers: new ApproverSource(ApprovalSources.Manager, Level: 1), Mode: ApprovalModes.Any);
    private static ApprovalNodeDef P(string id, string kind) => new(id, kind);
    private static ApprovalNodeDef JoinDef(string id, string forkId, string mode) => new(id, ApprovalNodeKinds.Join, ForkId: forkId, Mode: mode);
    private static ApprovalEdgeDef E(string id, string from, string to) => new(id, from, to);

    private static ApprovalGraphRuntime Build(List<ApprovalNodeDef> nodes, List<ApprovalEdgeDef> edges)
    {
        var kinds = new HashSet<string>
        {
            ApprovalNodeKinds.Start, ApprovalNodeKinds.End, ApprovalNodeKinds.Approval, ApprovalNodeKinds.Condition, ApprovalNodeKinds.Fork, ApprovalNodeKinds.Join
        };
        var g = ApprovalGraphRuntime.Build(new ApprovalDefinition(ApprovalShapes.Graph, null, null, null, [], nodes, edges), kinds, out var errors);
        errors.Should().BeEmpty();
        return g!;
    }

    /// <summary>start → f → 三條分支 a、b、c（各一個簽核）→ j → end。</summary>
    private static ApprovalGraphRuntime ThreeArms(string mode) => Build(
        [P("start", ApprovalNodeKinds.Start), P("f", ApprovalNodeKinds.Fork), Appr("a"), Appr("b"), Appr("c"), JoinDef("j", "f", mode), P("end", ApprovalNodeKinds.End)],
        [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("fc", "f", "c"), E("aj", "a", "j"), E("bj", "b", "j"), E("cj", "c", "j"), E("je", "j", "end")]);

    [Fact]
    public void CancelledRoots_AnyJoinPassed_SiblingRoots()
    {
        var g = ThreeArms(ApprovalModes.Any);
        var runs = new[] { Fork(1, "f", ""), Run(2, "a", "fa", Ok), JoinRun(3, "j", "", Passed, "aj"), Run(4, "b", "fb", Closed) };
        ApprovalGraphPath.CancelledRoots(g, runs).Should().BeEquivalentTo(new[] { "fb", "fc" });
    }

    [Fact]
    public void CancelledRoots_AnyJoinStillWaitingOrAbsent_Empty()
    {
        var g = ThreeArms(ApprovalModes.Any);
        ApprovalGraphPath.CancelledRoots(g, [Fork(1, "f", ""), Run(2, "a", "fa", Act)]).Should().BeEmpty();
    }

    [Fact]
    public void CancelledRoots_AllJoin_Empty()
    {
        var g = ThreeArms(ApprovalModes.All);
        var runs = new[]
        {
            Fork(1, "f", ""), JoinRun(2, "j", "", Passed, "aj"), JoinRun(3, "j", "", Passed, "bj"), JoinRun(4, "j", "", Passed, "cj")
        };
        ApprovalGraphPath.CancelledRoots(g, runs).Should().BeEmpty();
    }

    /// <summary>start → f → { x, f2 → { y, z } → j2(any) } → j(any) → end：外層分支 ff2 內有一個內層並行區。</summary>
    private static ApprovalGraphRuntime Nested() => Build(
        [P("start", ApprovalNodeKinds.Start), P("f", ApprovalNodeKinds.Fork), Appr("x"), P("f2", ApprovalNodeKinds.Fork), Appr("y"), Appr("z"),
         JoinDef("j2", "f2", ApprovalModes.Any), JoinDef("j", "f", ApprovalModes.Any), P("end", ApprovalNodeKinds.End)],
        [E("e0", "start", "f"), E("fx", "f", "x"), E("ff2", "f", "f2"), E("f2y", "f2", "y"), E("f2z", "f2", "z"),
         E("yj2", "y", "j2"), E("zj2", "z", "j2"), E("j2j", "j2", "j"), E("xj", "x", "j"), E("je", "j", "end")]);

    [Fact]
    public void CancelledRoots_NestedRegionInsideCancelled_CoveredByWithin()
    {
        var g = Nested();
        // x 先到外層匯合：外層另一條分支 ff2 整個取消；內層的段是它的子段，Within 涵蓋。
        var runs = new[]
        {
            Fork(1, "f", ""), Fork(2, "f2", "ff2"), Run(3, "y", "ff2/f2y", Act), Run(4, "z", "ff2/f2z", Act),
            Run(5, "x", "fx", Ok), JoinRun(6, "j", "", Passed, "xj")
        };
        var roots = ApprovalGraphPath.CancelledRoots(g, runs);
        roots.Should().BeEquivalentTo(new[] { "ff2" });
        runs.Where(r => roots.Any(root => ApprovalGraphRuntime.Within(r.BranchKey!, root))).Select(r => r.Seq).Should().Equal(2, 3, 4);
    }

    [Fact]
    public void CancelledRoots_InnerAnyJoin_ChildOfInnerOuterKey()
    {
        var g = Nested();
        // 內層任一到齊：到達列的 BranchKey 是內層分支節點所在的段 ff2，被取消的是 ff2/f2z。
        var runs = new[] { Fork(1, "f", ""), Fork(2, "f2", "ff2"), Run(3, "y", "ff2/f2y", Ok), JoinRun(4, "j2", "ff2", Passed, "yj2") };
        ApprovalGraphPath.CancelledRoots(g, runs).Should().BeEquivalentTo(new[] { "ff2/f2z" });
    }

    [Fact]
    public void CancelledRoots_EmptyBranchArrived_SiblingsCancelled()
    {
        var g = Build(
            [P("start", ApprovalNodeKinds.Start), P("f", ApprovalNodeKinds.Fork), Appr("b"), JoinDef("j", "f", ApprovalModes.Any), P("end", ApprovalNodeKinds.End)],
            [E("e0", "start", "f"), E("fj", "f", "j"), E("fb", "f", "b"), E("bj", "b", "j"), E("je", "j", "end")]);
        var runs = new[] { Fork(1, "f", ""), JoinRun(2, "j", "", Passed, "fj"), Run(3, "b", "fb", Closed) };
        ApprovalGraphPath.CancelledRoots(g, runs).Should().BeEquivalentTo(new[] { "fb" });
    }

    [Fact]
    public void ArrivedArms_TwoInEdgesSameArm_CountedOnce()
    {
        var g = Build(
            [P("start", ApprovalNodeKinds.Start), P("f", ApprovalNodeKinds.Fork), P("c", ApprovalNodeKinds.Condition), Appr("a1"), Appr("b"),
             JoinDef("j", "f", ApprovalModes.All), P("end", ApprovalNodeKinds.End)],
            [E("e0", "start", "f"), E("fc", "f", "c"), E("fb", "f", "b"),
             new("ca", "c", "a1", When: System.Text.Json.JsonDocument.Parse("""{"x":1}""").RootElement, Order: 1), new("cj", "c", "j", Default: true),
             E("a1j", "a1", "j"), E("bj", "b", "j"), E("je", "j", "end")]);
        var runs = new[] { JoinRun(1, "j", "", Waiting, "cj"), JoinRun(2, "j", "", Waiting, "a1j"), JoinRun(3, "j", "", Waiting, "bj") };
        ApprovalGraphPath.ArrivedArms(g, runs, "j", "").Should().BeEquivalentTo(new[] { "fc", "fb" });
    }

    [Fact]
    public void ArrivedArms_OtherOuterKey_NotCounted()
    {
        var g = ThreeArms(ApprovalModes.All);
        var runs = new[] { JoinRun(1, "j", "fx", Waiting, "aj"), JoinRun(2, "j", "fy", Waiting, "bj"), JoinRun(3, "j", "fx", Waiting, "cj") };
        ApprovalGraphPath.ArrivedArms(g, runs, "j", "fx").Should().BeEquivalentTo(new[] { "fa", "fc" });
        ApprovalGraphPath.ArrivedArms(g, runs, "j", "fy").Should().BeEquivalentTo(new[] { "fb" });
    }

    [Fact]
    public void ArrivedArms_ClosedArrivalRow_NotCounted()
    {
        var g = ThreeArms(ApprovalModes.All);
        var runs = new[] { JoinRun(1, "j", "", Closed, "aj"), JoinRun(2, "j", "", Waiting, "bj") };
        ApprovalGraphPath.ArrivedArms(g, runs, "j", "").Should().BeEquivalentTo(new[] { "fb" });
    }

    [Fact]
    public void Live_ActiveWaitingQueued()
    {
        var runs = new[] { Run(1, "a", "", Ok), Run(2, "b", "", Act), JoinRun(3, "j", "", Waiting, "e"), Run(4, "c", "", ApprovalStageStatus.Queued), Run(5, "d", "", Closed), Run(6, "e", "", Skip) };
        ApprovalGraphPath.Live(runs).Select(r => r.Seq).Should().Equal(2, 3, 4);
    }

    // ---- CanWithdraw ----

    private static ApprovalTask T(long runId, string status) => new() { Id = runId * 10, StageRunId = runId, Status = status };

    [Fact]
    public void CanWithdraw_NoActionOnActives_True()
    {
        var runs = new[] { Fork(1, "f", ""), Run(2, "a", "e1", Act), Run(3, "b", "e2", Act) };
        ApprovalGraphPath.CanWithdraw(runs, [T(2, ApprovalTaskStatus.Open), T(3, ApprovalTaskStatus.Open)]).Should().BeTrue();
    }

    [Fact]
    public void CanWithdraw_OneBranchWaitingAtJoin_True()
    {
        var runs = new[] { Fork(1, "f", ""), Run(2, "a", "e1", Ok), JoinRun(3, "j", "", Waiting, "e7"), Run(4, "b", "e2", Act) };
        ApprovalGraphPath.CanWithdraw(runs, [T(2, ApprovalTaskStatus.Approved), T(4, ApprovalTaskStatus.Open)]).Should().BeTrue();
    }

    [Fact]
    public void CanWithdraw_SeatApprovedInAllMode_False()
    {
        var runs = new[] { Run(1, "a", "e1", Act), Run(2, "b", "e2", Act) };
        ApprovalGraphPath.CanWithdraw(runs, [T(1, ApprovalTaskStatus.Open), T(2, ApprovalTaskStatus.Approved), T(2, ApprovalTaskStatus.Open)]).Should().BeFalse();
        ApprovalGraphPath.CanWithdraw(runs, [T(1, ApprovalTaskStatus.Rejected)]).Should().BeFalse();
    }

    [Fact]
    public void CanWithdraw_PreviousNodeApproved_True()
    {
        var runs = new[] { Run(1, "a", "", Ok), Run(2, "b", "", Act) };
        ApprovalGraphPath.CanWithdraw(runs, [T(1, ApprovalTaskStatus.Approved), T(2, ApprovalTaskStatus.Open)]).Should().BeTrue();
    }

    [Fact]
    public void CanWithdraw_AddSignActiveWithApprovedTask_False()
    {
        var runs = new[] { Run(1, "a", "", Ok), Run(2, "a", "", Act, addSign: true) };
        ApprovalGraphPath.CanWithdraw(runs, [T(2, ApprovalTaskStatus.Approved)]).Should().BeFalse();
    }
}
