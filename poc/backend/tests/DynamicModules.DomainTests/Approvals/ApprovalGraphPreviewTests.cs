using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>預計路徑（流程圖設計 §8.9、計畫 fg2 J19、J26、U9、U10）：手造定義，不碰資料庫。</summary>
public class ApprovalGraphPreviewTests
{
    private static readonly IReadOnlySet<string> Kinds = new HashSet<string>
    {
        ApprovalNodeKinds.Start, ApprovalNodeKinds.End, ApprovalNodeKinds.Approval,
        ApprovalNodeKinds.Condition, ApprovalNodeKinds.Fork, ApprovalNodeKinds.Join
    };

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ApprovalNodeDef Start() => new("start", ApprovalNodeKinds.Start);
    private static ApprovalNodeDef End(string id = "end") => new(id, ApprovalNodeKinds.End);
    private static ApprovalNodeDef Appr(string id) =>
        new(id, ApprovalNodeKinds.Approval, new I18nText(), Approvers: new ApproverSource(ApprovalSources.Manager, Level: 1), Mode: ApprovalModes.Any);
    private static ApprovalNodeDef Cond(string id) => new(id, ApprovalNodeKinds.Condition);
    private static ApprovalNodeDef Fork(string id) => new(id, ApprovalNodeKinds.Fork);
    private static ApprovalNodeDef Join(string id, string forkId, string mode = ApprovalModes.All) => new(id, ApprovalNodeKinds.Join, ForkId: forkId, Mode: mode);

    private static ApprovalEdgeDef E(string id, string from, string to) => new(id, from, to);
    /// <summary>條件出線：t 是 yes（成立）、no（不成立）、unknown（無法判斷）；hidden 為 true 時另帶 h 標記，表示引用發起人看不到的欄位。</summary>
    private static ApprovalEdgeDef C(string id, string from, string to, string t, int order, bool hidden = false) =>
        new(id, from, to, When: J(hidden ? $$"""{"t":"{{t}}","h":true}""" : $$"""{"t":"{{t}}"}"""), Order: order);
    private static ApprovalEdgeDef D(string id, string from, string to) => new(id, from, to, Default: true);

    private static ApprovalGraphRuntime Build(List<ApprovalNodeDef> nodes, List<ApprovalEdgeDef> edges)
    {
        var g = ApprovalGraphRuntime.Build(new ApprovalDefinition(ApprovalShapes.Graph, null, null, null, [], nodes, edges), Kinds, out var errors);
        errors.Should().BeEmpty();
        return g!;
    }

    private static ConditionTruth Eval(JsonElement w) => w.GetProperty("t").GetString() switch
    {
        "yes" => ConditionTruth.Holds, "no" => ConditionTruth.Fails, _ => ConditionTruth.Undecidable
    };

    private static bool Hidden(JsonElement w) => w.TryGetProperty("h", out _);

    private static PreviewWalk Walk(ApprovalGraphRuntime g, params string[] skipped) =>
        ApprovalGraphPreview.Walk(g, Eval, Hidden, n => skipped.Contains(n.Id));

    private static IEnumerable<string> Ids(PreviewWalk w) => w.Steps.Select(s => s.NodeId);

    // ───────── Walk ─────────

    [Fact]
    public void Walk_Linear()
    {
        var g = Build([Start(), Appr("a"), Appr("b"), End()], [E("e1", "start", "a"), E("e2", "a", "b"), E("e3", "b", "end")]);

        var w = Walk(g);

        Ids(w).Should().Equal("a", "b");
        w.Steps.Should().OnlyContain(s => !s.Undecided && s.AnyGroup == null);
        w.EdgeIds.Should().Equal("e1", "e2", "e3");
        w.NodeIds.Should().Equal("start", "a", "b", "end");
        w.UndecidableNodeId.Should().BeNull();
    }

    [Fact]
    public void Walk_ConditionChoosesEdge()
    {
        var g = Build([Start(), Cond("c"), Appr("big"), Appr("small"), End(), End("end2")],
            [E("e1", "start", "c"), C("e2", "c", "big", "yes", 1), D("e3", "c", "small"), E("e4", "big", "end"), E("e5", "small", "end2")]);

        var w = Walk(g);

        Ids(w).Should().Equal("big");
        w.EdgeIds.Should().Equal("e1", "e2", "e4");
    }

    [Fact]
    public void Walk_AllJoin_BothBranches_DefinitionOrder()
    {
        var g = Build([Start(), Fork("f"), Appr("a"), Appr("b"), Join("j", "f"), Appr("z"), End()],
            [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("jz", "j", "z"), E("ze", "z", "end")]);

        var w = Walk(g);

        Ids(w).Should().Equal("a", "b", "z");
        w.Steps.Should().OnlyContain(s => s.AnyGroup == null);
        w.EdgeIds.Should().BeEquivalentTo(["e0", "fa", "fb", "aj", "bj", "jz", "ze"]);
        w.NodeIds.Should().Equal("start", "f", "a", "b", "j", "z", "end");
    }

    [Fact]
    public void Walk_HiddenCondition_UndecidedAndResumeAtPostdominator()
    {
        // 條件 c 分到 x 或 y，兩條路之後都經過 g：從 g 接著列，不列 x、y
        var g = Build([Start(), Appr("m"), Cond("c"), Appr("x"), Appr("y"), Appr("g"), End()],
            [E("e1", "start", "m"), E("e2", "m", "c"), C("e3", "c", "x", "yes", 1, hidden: true), D("e4", "c", "y"),
             E("e5", "x", "g"), E("e6", "y", "g"), E("e7", "g", "end")]);

        var w = Walk(g);

        Ids(w).Should().Equal("m", "c", "g");
        w.Steps.Select(s => s.Undecided).Should().Equal(false, true, false);
        w.EdgeIds.Should().NotContain(["e3", "e4"]);
    }

    [Fact]
    public void Walk_HiddenCondition_ReachableButNotPostdominator_NotListed()
    {
        // 審查的反例：c1 分到 A、B；A 之後可選「總經理」或「直接結束」，B 一定經總經理。總經理在可達集合裡，卻不是必經
        var g = Build([Start(), Cond("c1"), Appr("a"), Cond("c2"), Appr("b"), Appr("gm"), End("end1"), End("end2")],
            [E("e1", "start", "c1"), C("e2", "c1", "a", "yes", 1, hidden: true), D("e3", "c1", "b"),
             E("e4", "a", "c2"), C("e5", "c2", "gm", "yes", 1), D("e6", "c2", "end1"), E("e7", "b", "gm"), E("e8", "gm", "end2")]);

        var w = Walk(g);

        Ids(w).Should().Equal("c1");
        w.Steps.Single().Undecided.Should().BeTrue();
    }

    [Fact]
    public void Walk_HiddenCondition_NoCommonSuccessor_Stops()
    {
        var g = Build([Start(), Cond("c"), Appr("a"), Appr("b"), End(), End("end2")],
            [E("e1", "start", "c"), C("e2", "c", "a", "yes", 1, hidden: true), D("e3", "c", "b"), E("e4", "a", "end"), E("e5", "b", "end2")]);

        var w = Walk(g);

        Ids(w).Should().Equal("c");
        w.UndecidableNodeId.Should().BeNull();
    }

    [Fact]
    public void Walk_HiddenConditionInsideBranch_ResumeAtJoin()
    {
        var g = Build([Start(), Fork("f"), Appr("a"), Cond("c"), Appr("x"), Appr("b"), Join("j", "f"), Appr("z"), End()],
            [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("ac", "a", "c"), C("cx", "c", "x", "yes", 1, hidden: true), D("cj", "c", "j"),
             E("xj", "x", "j"), E("bj", "b", "j"), E("jz", "j", "z"), E("ze", "z", "end")]);

        var w = Walk(g);

        Ids(w).Should().Equal("a", "c", "b", "z");
        w.Steps.Single(s => s.NodeId == "c").Undecided.Should().BeTrue();
    }

    [Fact]
    public void Walk_AnyJoin_ImmediateBranch_NoBranchesListed()
    {
        // 分支 s 確定不經人工就到達匯合：不列人工分支 a，只列 s 上被略過的節點，匯合之後照列
        var g = Build([Start(), Appr("m"), Fork("f"), Appr("a"), Appr("s"), Join("j", "f", ApprovalModes.Any), Appr("z"), End()],
            [E("e1", "start", "m"), E("e2", "m", "f"), E("fa", "f", "a"), E("fs", "f", "s"), E("aj", "a", "j"), E("sj", "s", "j"),
             E("jz", "j", "z"), E("ze", "z", "end")]);

        var w = Walk(g, "s");

        Ids(w).Should().Equal("m", "s", "z");
        w.Steps.Should().OnlyContain(s => s.AnyGroup == null);
        w.EdgeIds.Should().NotContain(["fa", "aj"]);
        w.NodeIds.Should().Equal("start", "m", "f", "s", "j", "z", "end");
    }

    [Fact]
    public void Walk_AnyJoin_ImmediateBranchLaterInOrder_StillNoBranchesListed()
    {
        // 立即到達的分支排在最後：排在它前面的人工分支雖會先建任務，但同一個交易內被取消
        var g = Build([Start(), Fork("f"), Appr("a"), Appr("b"), Appr("s"), Join("j", "f", ApprovalModes.Any), End()],
            [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("fs", "f", "s"), E("aj", "a", "j"), E("bj", "b", "j"), E("sj", "s", "j"), E("je", "j", "end")]);

        var w = Walk(g, "s");

        Ids(w).Should().Equal("s");
        w.NodeIds.Should().NotContain(["a", "b"]);
    }

    [Fact]
    public void Walk_AnyJoin_ArmsAfterTheImmediateOne_NotEvaluated()
    {
        // 引擎在立即到達的分支之後不再展開：排在後面的分支即使條件無法判斷，也不是預覽的問題
        var g = Build([Start(), Fork("f"), Appr("s"), Cond("c"), Appr("x"), Join("j", "f", ApprovalModes.Any), End()],
            [E("e0", "start", "f"), E("fs", "f", "s"), E("fc", "f", "c"), C("cx", "c", "x", "unknown", 1), D("cj", "c", "j"),
             E("xj", "x", "j"), E("sj", "s", "j"), E("je", "j", "end")]);

        var w = Walk(g, "s");

        w.UndecidableNodeId.Should().BeNull();
        Ids(w).Should().Equal("s");
    }

    [Fact]
    public void Walk_AnyJoin_NoImmediate_AllBranchesGrouped()
    {
        var g = Build([Start(), Fork("f"), Appr("a"), Appr("b"), Join("j", "f", ApprovalModes.Any), Appr("z"), End()],
            [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("jz", "j", "z"), E("ze", "z", "end")]);

        var w = Walk(g);

        Ids(w).Should().Equal("a", "b", "z");
        w.Steps.Select(s => s.AnyGroup).Should().Equal("j", "j", null);
    }

    [Fact]
    public void Walk_AnyJoin_HiddenConditionInBranch_NotImmediate_Grouped()
    {
        // 分支 1 有看不到的條件、分支 2 要人工：誰都不是確定立即到達
        var g = Build([Start(), Fork("f"), Cond("c"), Appr("x"), Appr("b"), Join("j", "f", ApprovalModes.Any), End()],
            [E("e0", "start", "f"), E("fc", "f", "c"), C("cx", "c", "x", "yes", 1, hidden: true), D("cj", "c", "j"),
             E("xj", "x", "j"), E("fb", "f", "b"), E("bj", "b", "j"), E("je", "j", "end")]);

        var w = Walk(g);

        Ids(w).Should().Equal("c", "b");
        w.Steps.Select(s => (s.Undecided, s.AnyGroup)).Should().Equal((true, "j"), (false, "j"));
    }

    [Fact]
    public void Walk_AnyJoin_HiddenArmThenImmediateArm_ImmediateWins()
    {
        var g = Build([Start(), Fork("f"), Cond("c"), Appr("x"), Appr("s"), Join("j", "f", ApprovalModes.Any), End()],
            [E("e0", "start", "f"), E("fc", "f", "c"), C("cx", "c", "x", "yes", 1, hidden: true), D("cj", "c", "j"),
             E("xj", "x", "j"), E("fs", "f", "s"), E("sj", "s", "j"), E("je", "j", "end")]);

        var w = Walk(g, "s");

        Ids(w).Should().Equal("s");
    }

    [Fact]
    public void Walk_NestedAllJoinInsideAnyGroup_InheritsOuterGroup()
    {
        var g = Build([Start(), Fork("f"), Appr("a"), Fork("f2"), Appr("p"), Appr("q"), Join("j2", "f2"), Join("j", "f", ApprovalModes.Any), End()],
            [E("e0", "start", "f"), E("fa", "f", "a"), E("f2e", "f", "f2"), E("aj", "a", "j"), E("f2p", "f2", "p"), E("f2q", "f2", "q"),
             E("pj", "p", "j2"), E("qj", "q", "j2"), E("j2j", "j2", "j"), E("je", "j", "end")]);

        var w = Walk(g);

        Ids(w).Should().Equal("a", "p", "q");
        w.Steps.Should().OnlyContain(s => s.AnyGroup == "j");
    }

    [Fact]
    public void Walk_UndecidableCondition_Reported()
    {
        var g = Build([Start(), Cond("c"), Appr("a"), Appr("b"), End(), End("end2")],
            [E("e1", "start", "c"), C("e2", "c", "a", "unknown", 1), D("e3", "c", "b"), E("e4", "a", "end"), E("e5", "b", "end2")]);

        var w = Walk(g);

        w.UndecidableNodeId.Should().Be("c");
    }

    // ───────── ResumeAfter ─────────

    [Fact]
    public void ResumeAfter_DiamondMerge()
    {
        var g = Build([Start(), Cond("c"), Appr("a"), Appr("b"), Appr("m"), End()],
            [E("e1", "start", "c"), C("e2", "c", "a", "yes", 1), D("e3", "c", "b"), E("e4", "a", "m"), E("e5", "b", "m"), E("e6", "m", "end")]);

        ApprovalGraphPreview.ResumeAfter(g, "c").Should().Be("m");
    }

    [Fact]
    public void ResumeAfter_MultipleEnds_Null()
    {
        var g = Build([Start(), Cond("c"), Appr("a"), Appr("b"), End(), End("end2")],
            [E("e1", "start", "c"), C("e2", "c", "a", "yes", 1), D("e3", "c", "b"), E("e4", "a", "end"), E("e5", "b", "end2")]);

        ApprovalGraphPreview.ResumeAfter(g, "c").Should().BeNull();
    }

    [Fact]
    public void ResumeAfter_NestedRegions()
    {
        // c 的兩條路：經過一個並行區再到 m，或直接到 m。直接後必經節點是 m，不是並行區裡的節點或匯合
        var g = Build([Start(), Cond("c"), Fork("f"), Appr("a"), Appr("b"), Join("j", "f"), Appr("m"), End()],
            [E("e1", "start", "c"), C("e2", "c", "f", "yes", 1), D("e3", "c", "m"), E("fa", "f", "a"), E("fb", "f", "b"),
             E("aj", "a", "j"), E("bj", "b", "j"), E("jm", "j", "m"), E("me", "m", "end")]);

        ApprovalGraphPreview.ResumeAfter(g, "c").Should().Be("m");
        ApprovalGraphPreview.ResumeAfter(g, "f").Should().Be("j");
    }
}
