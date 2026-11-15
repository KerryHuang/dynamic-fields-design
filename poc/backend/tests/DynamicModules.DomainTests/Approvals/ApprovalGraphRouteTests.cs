using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>graph 每次同意的路線比較（流程圖設計 §8.7、計畫 fg2 J13、J27）：手造定義與節點實例，不碰資料庫。</summary>
public class ApprovalGraphRouteTests
{
    private const string Ok = ApprovalStageStatus.Approved, Skip = ApprovalStageStatus.Skipped, Ret = ApprovalStageStatus.Returned,
        Act = ApprovalStageStatus.Active, Closed = ApprovalStageStatus.Closed, Passed = ApprovalStageStatus.Passed, Waiting = ApprovalStageStatus.Waiting;

    private static readonly IReadOnlySet<string> Kinds = new HashSet<string>
    {
        ApprovalNodeKinds.Start, ApprovalNodeKinds.End, ApprovalNodeKinds.Approval,
        ApprovalNodeKinds.Condition, ApprovalNodeKinds.Fork, ApprovalNodeKinds.Join
    };

    // 條件寫成 {"k":"big"}；eval 看 k：在 holding 裡就成立，"undec" 無法判斷，其餘不成立。
    private static JsonElement W(string k) => JsonDocument.Parse($$"""{"k":"{{k}}"}""").RootElement.Clone();

    private static Func<JsonElement, ConditionTruth> Ev(params string[] holding) => je =>
    {
        var k = je.GetProperty("k").GetString()!;
        return k == "undec" ? ConditionTruth.Undecidable : holding.Contains(k) ? ConditionTruth.Holds : ConditionTruth.Fails;
    };

    // keys 裡的條件無法判斷，其餘不成立。
    private static Func<JsonElement, ConditionTruth> Undec(params string[] keys) =>
        je => keys.Contains(je.GetProperty("k").GetString()!) ? ConditionTruth.Undecidable : ConditionTruth.Fails;

    private static ApprovalNodeDef Appr(string id, string? when = null) =>
        new(id, ApprovalNodeKinds.Approval, new I18nText(), When: when is null ? null : W(when),
            Approvers: new ApproverSource(ApprovalSources.Manager, Level: 1), Mode: ApprovalModes.Any);
    private static ApprovalNodeDef Plain(string id, string kind) => new(id, kind);
    private static ApprovalNodeDef Join(string id, string forkId, string mode = ApprovalModes.All) => new(id, ApprovalNodeKinds.Join, ForkId: forkId, Mode: mode);
    private static ApprovalEdgeDef E(string id, string from, string to) => new(id, from, to);
    private static ApprovalEdgeDef Case(string id, string from, string to, string when, int order) => new(id, from, to, When: W(when), Order: order);
    private static ApprovalEdgeDef Default(string id, string from, string to) => new(id, from, to, Default: true);

    private static ApprovalGraphRuntime Build(List<ApprovalNodeDef> nodes, List<ApprovalEdgeDef> edges)
    {
        var g = ApprovalGraphRuntime.Build(new ApprovalDefinition(ApprovalShapes.Graph, null, null, null, [], nodes, edges), Kinds, out var errors);
        errors.Should().BeEmpty();
        return g!;
    }

    private static ApprovalStageRun Run(int seq, string nodeId, string branch, string status, string kind = ApprovalNodeKinds.Approval,
        string? inEdge = null, string? skipReason = null) =>
        new() { Id = seq, Seq = seq, StageCode = nodeId, NodeId = nodeId, BranchKey = branch, Status = status, NodeKind = kind, InEdgeId = inEdge, SkipReason = skipReason };

    private static ApprovalStageRun Cond(int seq, string id, string branch, string inEdge) => Run(seq, id, branch, Passed, ApprovalNodeKinds.Condition, inEdge);
    private static ApprovalStageRun ForkRun(int seq, string id, string branch, string inEdge) => Run(seq, id, branch, Passed, ApprovalNodeKinds.Fork, inEdge);
    private static ApprovalStageRun JoinRun(int seq, string id, string outer, string status, string inEdge) => Run(seq, id, outer, status, ApprovalNodeKinds.Join, inEdge);
    private static ApprovalStageRun SkippedByCondition(int seq, string id, string branch, string inEdge) => Run(seq, id, branch, Skip, inEdge: inEdge, skipReason: "condition");

    /// <summary>start → m → c（big → a；預設 → b）→ end。</summary>
    private static ApprovalGraphRuntime Choice() => Build(
        [Plain("start", ApprovalNodeKinds.Start), Appr("m"), Plain("c", ApprovalNodeKinds.Condition), Appr("a"), Appr("b"), Plain("end", ApprovalNodeKinds.End)],
        [E("e1", "start", "m"), E("e2", "m", "c"), Case("e3", "c", "a", "big", 1), Default("e4", "c", "b"), E("e5", "a", "end"), E("e6", "b", "end")]);

    /// <summary>start → s（略過條件 sk）→ end。</summary>
    private static ApprovalGraphRuntime SkipNode() => Build(
        [Plain("start", ApprovalNodeKinds.Start), Appr("s", "sk"), Plain("end", ApprovalNodeKinds.End)],
        [E("e1", "start", "s"), E("e2", "s", "end")]);

    // ---- 條件節點選線、簽核節點略過條件 ----

    [Fact]
    public void ConditionNowChoosesOtherEdge_Changed()
    {
        var runs = new[] { Run(1, "m", "", Ok, inEdge: "e1"), Cond(2, "c", "", "e2"), Run(3, "b", "", Act, inEdge: "e4") };
        ApprovalGraphRoute.Changed(Choice(), runs, Ev("big")).Should().Be(new GraphRouteCheck(true, null));
    }

    [Fact]
    public void ConditionSameEdge_NotChanged()
    {
        var runs = new[] { Run(1, "m", "", Ok, inEdge: "e1"), Cond(2, "c", "", "e2"), Run(3, "b", "", Act, inEdge: "e4") };
        ApprovalGraphRoute.Changed(Choice(), runs, Ev()).Should().Be(new GraphRouteCheck(false, null));
        // 當時選的是 big 那條，現在仍成立
        var big = new[] { Run(1, "m", "", Ok, inEdge: "e1"), Cond(2, "c", "", "e2"), Run(3, "a", "", Act, inEdge: "e3") };
        ApprovalGraphRoute.Changed(Choice(), big, Ev("big")).Should().Be(new GraphRouteCheck(false, null));
    }

    [Fact]
    public void SkippedNodeConditionNowHolds_Changed()
    {
        var runs = new[] { SkippedByCondition(1, "s", "", "e1") };
        ApprovalGraphRoute.Changed(SkipNode(), runs, Ev("sk")).Should().Be(new GraphRouteCheck(true, null));
    }

    [Fact]
    public void SkippedNodeConditionStillFails_NotChanged()
    {
        var runs = new[] { SkippedByCondition(1, "s", "", "e1") };
        ApprovalGraphRoute.Changed(SkipNode(), runs, Ev()).Should().Be(new GraphRouteCheck(false, null));
    }

    [Fact]
    public void SkippedByEmptyApprovers_NotAboutCondition_Ignored()
    {
        var runs = new[] { Run(1, "s", "", Skip, inEdge: "e1", skipReason: "empty") };
        ApprovalGraphRoute.Changed(SkipNode(), runs, Ev("sk")).Should().Be(new GraphRouteCheck(false, null));
    }

    [Fact]
    public void ApprovedNodeConditionNowFails_NotChanged()
    {
        // 多簽不算少簽：已核准的節點，略過條件現在不成立也不算路線不同
        var runs = new[] { Run(1, "s", "", Ok, inEdge: "e1") };
        ApprovalGraphRoute.Changed(SkipNode(), runs, Ev()).Should().Be(new GraphRouteCheck(false, null));
    }

    [Fact]
    public void SkippedNodeConditionUnparseable_CountsAsHolds_Changed()
    {
        var runs = new[] { SkippedByCondition(1, "s", "", "e1") };
        ApprovalGraphRoute.Changed(SkipNode(), runs, Undec("sk")).Should().Be(new GraphRouteCheck(true, null));
    }

    [Fact]
    public void ConditionNodeUndecidable_NotTreatedAsHolds()
    {
        // 同一個 eval：略過條件的節點把 Undecidable 當成立，條件節點的出線則回 UndecidableNodeId，而且不算 Changed
        var g = Build(
            [Plain("start", ApprovalNodeKinds.Start), Appr("s", "undec"), Plain("c", ApprovalNodeKinds.Condition), Appr("a"), Appr("b"), Plain("end", ApprovalNodeKinds.End)],
            [E("e1", "start", "s"), E("e2", "s", "c"), Case("e3", "c", "a", "undec", 1), Default("e4", "c", "b"), E("e5", "a", "end"), E("e6", "b", "end")]);
        var runs = new[] { SkippedByCondition(1, "s", "", "e1"), Cond(2, "c", "", "e2"), Run(3, "b", "", Act, inEdge: "e4") };
        ApprovalGraphRoute.Changed(g, runs, Ev()).Should().Be(new GraphRouteCheck(false, "c"));
    }

    [Fact]
    public void ConditionUndecidable_ReportsNodeId()
    {
        var runs = new[] { Run(1, "m", "", Ok, inEdge: "e1"), Cond(2, "c", "", "e2"), Run(3, "b", "", Act, inEdge: "e4") };
        ApprovalGraphRoute.Changed(Choice(), runs, Undec("big")).Should().Be(new GraphRouteCheck(false, "c"));
    }

    [Fact]
    public void ReturnCut_UsesLatestConditionRun()
    {
        // 第一次選 a、a 被退回上一關（m 重來），第二次選 b；只看最新一列的選擇，舊的 c（選 a）已被截掉
        var runs = new[]
        {
            Run(1, "m", "", Ok, inEdge: "e1"), Cond(2, "c", "", "e2"), Run(3, "a", "", Ret, inEdge: "e3"),
            Run(4, "m", "", Ok, inEdge: "e1"), Cond(5, "c", "", "e2"), Run(6, "b", "", Act, inEdge: "e4")
        };
        ApprovalGraphRoute.Changed(Choice(), runs, Ev()).Should().Be(new GraphRouteCheck(false, null));
        ApprovalGraphRoute.Changed(Choice(), runs, Ev("big")).Changed.Should().BeTrue();
    }

    // ---- 並行：被取消的分支不參與 ----

    /// <summary>start → f → 兩條分支：x（單一簽核）、p（略過條件 sk）→ c（big → y1；預設 → y2）→ 匯合 j → end。</summary>
    private static ApprovalGraphRuntime Fork(string joinMode) => Build(
        [Plain("start", ApprovalNodeKinds.Start), Plain("f", ApprovalNodeKinds.Fork), Appr("x"), Appr("p", "sk"), Plain("c", ApprovalNodeKinds.Condition),
         Appr("y1"), Appr("y2"), Join("j", "f", joinMode), Plain("end", ApprovalNodeKinds.End)],
        [E("e0", "start", "f"), E("fx", "f", "x"), E("fp", "f", "p"), E("pc", "p", "c"), Case("cy1", "c", "y1", "big", 1), Default("cy2", "c", "y2"),
         E("xj", "x", "j"), E("y1j", "y1", "j"), E("y2j", "y2", "j"), E("je", "j", "end")]);

    private static IEnumerable<ApprovalStageRun> ForkRuns(string status = Act) =>
    [
        ForkRun(1, "f", "", "e0"), Run(2, "x", "fx", Ok, inEdge: "fx"),
        SkippedByCondition(3, "p", "fp", "fp"), Cond(4, "c", "fp", "pc"), Run(5, "y2", "fp", status, inEdge: "cy2")
    ];

    [Fact]
    public void CancelledBranchChanges_Ignored()
    {
        // 任一到齊：x 先核准、匯合通過，p 那條分支被取消；其上的條件節點改選、略過條件變成立都不算
        var runs = ForkRuns(Closed).Append(JoinRun(6, "j", "", Passed, "xj")).ToList();
        ApprovalGraphRoute.Changed(Fork(ApprovalModes.Any), runs, Ev("big", "sk")).Should().Be(new GraphRouteCheck(false, null));
    }

    [Fact]
    public void BranchNotCancelled_SameChanges_Counted()
    {
        // 對照組：全部到齊的匯合沒有取消任何分支，分支內的條件改選與略過條件都算
        var runs = ForkRuns().Append(JoinRun(6, "j", "", Waiting, "xj")).ToList();
        ApprovalGraphRoute.Changed(Fork(ApprovalModes.All), runs, Ev("big")).Changed.Should().BeTrue();
        ApprovalGraphRoute.Changed(Fork(ApprovalModes.All), runs, Ev("sk")).Changed.Should().BeTrue();
        ApprovalGraphRoute.Changed(Fork(ApprovalModes.All), runs, Ev()).Changed.Should().BeFalse();
    }

    [Fact]
    public void EmptyBranchAnyPassed_CancelledBranchChange_Ignored()
    {
        // 空分支（f → j 直連）讓任一到齊在送出當下就通過；前面展開的分支被取消，其上的條件變化不觸發
        var g = Build(
            [Plain("start", ApprovalNodeKinds.Start), Plain("f", ApprovalNodeKinds.Fork), Plain("c", ApprovalNodeKinds.Condition),
             Appr("y1"), Appr("y2"), Join("j", "f", ApprovalModes.Any), Plain("end", ApprovalNodeKinds.End)],
            [E("e0", "start", "f"), E("fc", "f", "c"), E("fj", "f", "j"), Case("cy1", "c", "y1", "big", 1), Default("cy2", "c", "y2"),
             E("y1j", "y1", "j"), E("y2j", "y2", "j"), E("je", "j", "end")]);
        var runs = new[]
        {
            ForkRun(1, "f", "", "e0"), Cond(2, "c", "fc", "fc"), Run(3, "y2", "fc", Closed, inEdge: "cy2"), JoinRun(4, "j", "", Passed, "fj")
        };
        ApprovalGraphRoute.Changed(g, runs, Ev("big")).Should().Be(new GraphRouteCheck(false, null));
    }

    /// <summary>外層 fo 兩條分支：oa 裡有內層 fi（ia：a；ib：c → y1 | y2），ob：z；內層匯合 ji 全部到齊、外層匯合 jo 由參數決定。</summary>
    private static ApprovalGraphRuntime Nested(string outerJoinMode) => Build(
        [Plain("start", ApprovalNodeKinds.Start), Plain("fo", ApprovalNodeKinds.Fork), Plain("fi", ApprovalNodeKinds.Fork), Appr("a"), Plain("c", ApprovalNodeKinds.Condition),
         Appr("y1"), Appr("y2"), Join("ji", "fi"), Appr("z"), Join("jo", "fo", outerJoinMode), Plain("end", ApprovalNodeKinds.End)],
        [E("e0", "start", "fo"), E("oa", "fo", "fi"), E("ob", "fo", "z"), E("ia", "fi", "a"), E("ib", "fi", "c"),
         Case("cy1", "c", "y1", "big", 1), Default("cy2", "c", "y2"), E("aji", "a", "ji"), E("y1ji", "y1", "ji"), E("y2ji", "y2", "ji"),
         E("jijo", "ji", "jo"), E("zjo", "z", "jo"), E("je", "jo", "end")]);

    private static IEnumerable<ApprovalStageRun> NestedRuns() =>
    [
        ForkRun(1, "fo", "", "e0"), ForkRun(2, "fi", "oa", "oa"), Run(3, "a", "oa/ia", Act, inEdge: "ia"),
        Cond(4, "c", "oa/ib", "ib"), Run(5, "y2", "oa/ib", Act, inEdge: "cy2"), Run(6, "z", "ob", Act, inEdge: "ob")
    ];

    [Fact]
    public void NestedJoin_InnerConditionChanged_Changed()
    {
        ApprovalGraphRoute.Changed(Nested(ApprovalModes.All), NestedRuns().ToList(), Ev("big")).Changed.Should().BeTrue();
        ApprovalGraphRoute.Changed(Nested(ApprovalModes.All), NestedRuns().ToList(), Ev()).Changed.Should().BeFalse();
    }

    [Fact]
    public void NestedJoin_InsideCancelledOuterBranch_Ignored()
    {
        // 外層任一到齊：z 先核准，oa（含整個內層並行區）被取消，內層的條件節點不參與
        var runs = NestedRuns().Take(5).Append(Run(6, "z", "ob", Ok, inEdge: "ob")).Append(JoinRun(7, "jo", "", Passed, "zjo")).ToList();
        ApprovalGraphRoute.Changed(Nested(ApprovalModes.Any), runs, Ev("big")).Should().Be(new GraphRouteCheck(false, null));
    }

    [Fact]
    public void ChosenEdge_InferredFromJoinArrivalRow()
    {
        // 條件節點的預設線直接接匯合：它之後第一列是外層段的匯合到達列（InEdgeId = 這條出線）
        var g = Build(
            [Plain("start", ApprovalNodeKinds.Start), Plain("f", ApprovalNodeKinds.Fork), Appr("x"), Plain("c", ApprovalNodeKinds.Condition), Appr("y"),
             Join("j", "f"), Plain("end", ApprovalNodeKinds.End)],
            [E("e0", "start", "f"), E("fx", "f", "x"), E("fc", "f", "c"), Case("cy", "c", "y", "big", 1), Default("cj", "c", "j"),
             E("xj", "x", "j"), E("yj", "y", "j"), E("je", "j", "end")]);
        var runs = new[]
        {
            ForkRun(1, "f", "", "e0"), Run(2, "x", "fx", Act, inEdge: "fx"), Cond(3, "c", "fc", "fc"), JoinRun(4, "j", "", Waiting, "cj")
        };
        ApprovalGraphRoute.Changed(g, runs, Ev("big")).Changed.Should().BeTrue();
        ApprovalGraphRoute.Changed(g, runs, Ev()).Changed.Should().BeFalse();
    }
}
