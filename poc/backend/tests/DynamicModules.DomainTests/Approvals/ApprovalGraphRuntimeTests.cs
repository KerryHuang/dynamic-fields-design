using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>執行期的圖（流程圖設計 §8、計畫 fg2 J3、J7、J8、J13）：手造定義，不碰資料庫。</summary>
public class ApprovalGraphRuntimeTests
{
    private static readonly IReadOnlySet<string> Fg2Kinds = new HashSet<string>
    {
        ApprovalNodeKinds.Start, ApprovalNodeKinds.End, ApprovalNodeKinds.Approval,
        ApprovalNodeKinds.Condition, ApprovalNodeKinds.Fork, ApprovalNodeKinds.Join
    };

    private static readonly IReadOnlySet<string> WithNotify = new HashSet<string>(Fg2Kinds) { ApprovalNodeKinds.Notify };

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ApprovalNodeDef Appr(string id) => new(id, ApprovalNodeKinds.Approval, new I18nText(), Approvers: new ApproverSource(ApprovalSources.Manager, Level: 1), Mode: ApprovalModes.Any);
    private static ApprovalNodeDef Plain(string id, string kind) => new(id, kind);
    private static ApprovalNodeDef Join(string id, string forkId, string mode = ApprovalModes.All) => new(id, ApprovalNodeKinds.Join, ForkId: forkId, Mode: mode);
    private static ApprovalEdgeDef E(string id, string from, string to) => new(id, from, to);

    private static ApprovalDefinition Def(List<ApprovalNodeDef> nodes, List<ApprovalEdgeDef> edges) =>
        new(ApprovalShapes.Graph, null, null, null, [], nodes, edges);

    private static ApprovalGraphRuntime Build(ApprovalDefinition d, IReadOnlySet<string>? kinds = null)
    {
        var g = ApprovalGraphRuntime.Build(d, kinds ?? Fg2Kinds, out var errors);
        errors.Should().BeEmpty();
        return g!;
    }

    /// <summary>start → fork f → 兩條分支（a、b 各一個簽核）→ join j → end。</summary>
    private static ApprovalDefinition SimpleFork(string joinMode = ApprovalModes.All) => Def(
        [Plain("start", ApprovalNodeKinds.Start), Plain("f", ApprovalNodeKinds.Fork), Appr("a"), Appr("b"), Join("j", "f", joinMode), Plain("end", ApprovalNodeKinds.End)],
        [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("je", "j", "end")]);

    /// <summary>設計 §7.1 的範例（含通知節點）。</summary>
    private static ApprovalDefinition DesignExample() => Def(
        [
            Plain("start", ApprovalNodeKinds.Start), Appr("mgr"), Plain("amount", ApprovalNodeKinds.Condition), Plain("split", ApprovalNodeKinds.Fork),
            Appr("legal"), Appr("fin"), Join("merge", "split"),
            new("tell-hr", ApprovalNodeKinds.Notify, new I18nText(),
                Recipients: [new NotifyRecipientDef(ApprovalNotifyRecipientKinds.Initiator)],
                Template: new NotifyTemplateDef(new I18nText(), new I18nText())),
            Plain("end", ApprovalNodeKinds.End)
        ],
        [
            E("e1", "start", "mgr"), E("e2", "mgr", "amount"),
            new("e3", "amount", "split", When: J("""{"all":[{"field":21,"op":"gte","value":100000}]}"""), Order: 1),
            new("e4", "amount", "tell-hr", Default: true),
            E("e5", "split", "legal"), E("e6", "split", "fin"), E("e7", "legal", "merge"), E("e8", "fin", "merge"),
            E("e9", "merge", "tell-hr"), E("e10", "tell-hr", "end")
        ]);

    [Fact]
    public void Build_DesignExample_Ok()
    {
        var g = ApprovalGraphRuntime.Build(DesignExample(), WithNotify, out var errors);
        errors.Should().BeEmpty();
        g!.Start.Id.Should().Be("start");
    }

    [Fact]
    public void Build_MissingDefaultEdge_NullWithErrors()
    {
        var d = Def(
            [Plain("start", ApprovalNodeKinds.Start), Plain("c", ApprovalNodeKinds.Condition), Appr("a"), Appr("b"), Plain("end", ApprovalNodeKinds.End)],
            [E("e0", "start", "c"),
             new("ca", "c", "a", When: J("""{"x":1}"""), Order: 1), new("cb", "c", "b", When: J("""{"x":2}"""), Order: 2),
             E("ae", "a", "end"), E("be", "b", "end")]);
        ApprovalGraphRuntime.Build(d, Fg2Kinds, out var errors).Should().BeNull();
        errors.Select(e => e.Code).Should().Contain("defaultCount");
    }

    [Fact]
    public void Build_NoNodes_Null()
    {
        ApprovalGraphRuntime.Build(new ApprovalDefinition(ApprovalShapes.Graph, null, null, null, []), Fg2Kinds, out var errors).Should().BeNull();
        errors.Should().NotBeEmpty();
        ApprovalGraphRuntime.Build(Def([], []), Fg2Kinds, out var errors2).Should().BeNull();
        errors2.Should().NotBeEmpty();
    }

    [Fact]
    public void Build_OverPolicyApprovalNodes_StillBuilds()
    {
        var nodes = new List<ApprovalNodeDef> { Plain("start", ApprovalNodeKinds.Start) };
        nodes.AddRange(Enumerable.Range(1, 12).Select(i => Appr($"a{i}")));
        nodes.Add(Plain("end", ApprovalNodeKinds.End));
        var edges = Enumerable.Range(0, nodes.Count - 1).Select(i => E($"e{i + 1}", nodes[i].Id, nodes[i + 1].Id)).ToList();
        ApprovalGraphRuntime.Build(Def(nodes, edges), Fg2Kinds, out var errors).Should().NotBeNull();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Build_NotifyNodeNotSupported_NullWithUnsupportedKind()
    {
        var g = ApprovalGraphRuntime.Build(DesignExample(), Fg2Kinds, out var errors);
        g.Should().BeNull();
        errors.Should().ContainSingle(e => e.Code == "unsupportedKind").Which.Key.Should().Be("nodes.tell-hr");
    }

    [Fact]
    public void Build_NotifyNodeSupported_Builds() =>
        ApprovalGraphRuntime.Build(DesignExample(), WithNotify, out _).Should().NotBeNull();

    [Fact]
    public void Out_KeepsDefinitionOrder()
    {
        var g = Build(DesignExample(), WithNotify);
        g.Out("split").Select(e => e.Id).Should().Equal("e5", "e6");
        g.Out("amount").Select(e => e.Id).Should().Equal("e3", "e4");
        g.Out("end").Should().BeEmpty();
        g.Edge("e9").To.Should().Be("tell-hr");
        g.Node("fin").Kind.Should().Be(ApprovalNodeKinds.Approval);
    }

    [Fact]
    public void JoinOf_ForkOf_Paired()
    {
        var g = Build(SimpleFork());
        g.JoinOf("f").Id.Should().Be("j");
        g.ForkOf("j").Id.Should().Be("f");
    }

    [Fact]
    public void ArmOf_DirectForkToJoin_IsThatEdge()
    {
        var d = Def(
            [Plain("start", ApprovalNodeKinds.Start), Plain("f", ApprovalNodeKinds.Fork), Appr("b"), Join("j", "f"), Plain("end", ApprovalNodeKinds.End)],
            [E("e0", "start", "f"), E("fj", "f", "j"), E("fb", "f", "b"), E("bj", "b", "j"), E("je", "j", "end")]);
        var g = Build(d);
        g.ArmOf("j", "fj").Should().Be("fj");
        g.ArmOf("j", "bj").Should().Be("fb");
    }

    [Fact]
    public void ArmOf_ThroughApproval_IsForkOutEdge()
    {
        var g = Build(SimpleFork());
        g.ArmOf("j", "aj").Should().Be("fa");
        g.ArmOf("j", "bj").Should().Be("fb");
    }

    /// <summary>分支 1 內有條件節點，它的兩條出線（一條經簽核、一條直接）都接匯合；分支 2 一個簽核。</summary>
    private static ApprovalDefinition ConditionIntoJoin() => Def(
        [Plain("start", ApprovalNodeKinds.Start), Plain("f", ApprovalNodeKinds.Fork), Plain("c", ApprovalNodeKinds.Condition), Appr("a1"), Appr("b"),
         Join("j", "f"), Plain("end", ApprovalNodeKinds.End)],
        [E("e0", "start", "f"), E("fc", "f", "c"), E("fb", "f", "b"),
         new("ca", "c", "a1", When: J("""{"x":1}"""), Order: 1), new("cj", "c", "j", Default: true),
         E("a1j", "a1", "j"), E("bj", "b", "j"), E("je", "j", "end")]);

    [Fact]
    public void ArmOf_ConditionTwoEdgesIntoJoin_SameArm()
    {
        var g = Build(ConditionIntoJoin());
        g.ArmOf("j", "cj").Should().Be("fc");
        g.ArmOf("j", "a1j").Should().Be("fc");
        g.ArmOf("j", "bj").Should().Be("fb");
    }

    [Fact]
    public void ArmOf_NestedInnerRegion_BelongsToOuterArm()
    {
        var d = Def(
            [Plain("start", ApprovalNodeKinds.Start), Plain("f", ApprovalNodeKinds.Fork), Appr("x"), Plain("f2", ApprovalNodeKinds.Fork),
             Appr("y"), Appr("z"), Join("j2", "f2"), Join("j", "f"), Plain("end", ApprovalNodeKinds.End)],
            [E("e0", "start", "f"), E("fx", "f", "x"), E("ff2", "f", "f2"), E("f2y", "f2", "y"), E("f2z", "f2", "z"),
             E("yj2", "y", "j2"), E("zj2", "z", "j2"), E("j2j", "j2", "j"), E("xj", "x", "j"), E("je", "j", "end")]);
        var g = Build(d);
        g.ArmOf("j", "j2j").Should().Be("ff2");
        g.ArmOf("j", "xj").Should().Be("fx");
        g.ArmOf("j2", "yj2").Should().Be("f2y");
    }

    [Fact]
    public void Child_RootAndNested()
    {
        ApprovalGraphRuntime.Child("", "e3").Should().Be("e3");
        ApprovalGraphRuntime.Child("e3", "e9").Should().Be("e3/e9");
    }

    [Fact]
    public void Within_SegmentBoundary()
    {
        ApprovalGraphRuntime.Within("e1", "e1").Should().BeTrue();
        ApprovalGraphRuntime.Within("e10", "e1").Should().BeFalse();
        ApprovalGraphRuntime.Within("e1/e9", "e1").Should().BeTrue();
        ApprovalGraphRuntime.Within("e1/e9", "e1/e9").Should().BeTrue();
        ApprovalGraphRuntime.Within("e1", "e1/e9").Should().BeFalse();
    }

    /// <summary>start → c → a、b（兩條有條件的線，陣列順序與 order 可自訂）、預設線 → d → end。</summary>
    private static ApprovalGraphRuntime CondGraph(int orderToA, int orderToB) => Build(Def(
        [Plain("start", ApprovalNodeKinds.Start), Plain("c", ApprovalNodeKinds.Condition), Appr("a"), Appr("b"), Appr("d"), Plain("end", ApprovalNodeKinds.End)],
        [E("e0", "start", "c"),
         new("ca", "c", "a", When: J("""{"t":"a"}"""), Order: orderToA), new("cb", "c", "b", When: J("""{"t":"b"}"""), Order: orderToB),
         new("cd", "c", "d", Default: true), E("ae", "a", "end"), E("be", "b", "end"), E("de", "d", "end")]));

    private static Func<JsonElement, ConditionTruth> Eval(string a, string b) => w =>
        w.GetProperty("t").GetString() switch { "a" => Parse(a), "b" => Parse(b), _ => ConditionTruth.Undecidable };

    private static ConditionTruth Parse(string s) => s switch { "holds" => ConditionTruth.Holds, "fails" => ConditionTruth.Fails, _ => ConditionTruth.Undecidable };

    [Fact]
    public void Choose_FirstHoldingByOrder()
    {
        var (edge, undecidable) = CondGraph(1, 2).Choose("c", Eval("fails", "holds"));
        edge!.Id.Should().Be("cb");
        undecidable.Should().BeFalse();
    }

    [Fact]
    public void Choose_NoneHolds_Default()
    {
        var (edge, undecidable) = CondGraph(1, 2).Choose("c", Eval("fails", "fails"));
        edge!.Id.Should().Be("cd");
        undecidable.Should().BeFalse();
    }

    [Fact]
    public void Choose_UndecidableBeforeHolding_Undecidable()
    {
        var (edge, undecidable) = CondGraph(1, 2).Choose("c", Eval("undecidable", "holds"));
        edge.Should().BeNull();
        undecidable.Should().BeTrue();
    }

    [Fact]
    public void Choose_HoldingBeforeUndecidable_ReturnsHolding()
    {
        var (edge, undecidable) = CondGraph(1, 2).Choose("c", Eval("holds", "undecidable"));
        edge!.Id.Should().Be("ca");
        undecidable.Should().BeFalse();
    }

    [Fact]
    public void Choose_OrderNotArrayOrder()
    {
        // 陣列裡 ca 在前，但 order 是 cb 較小：兩條都成立時選 cb。
        var (edge, _) = CondGraph(2, 1).Choose("c", Eval("holds", "holds"));
        edge!.Id.Should().Be("cb");
    }
}
