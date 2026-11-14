using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>案例檔放不下的：技術上限、與舊版比較、轉換後的舊 sequence。</summary>
public class ApprovalGraphValidatorTests
{
    private static readonly GraphPolicyLimits Policy = new(10, 5);

    private static ApprovalNodeDef Appr(string id) => new(id, ApprovalNodeKinds.Approval);

    private static ApprovalDefinition Def(List<ApprovalNodeDef> nodes, List<ApprovalEdgeDef> edges) =>
        new(ApprovalShapes.Graph, null, null, null, [], nodes, edges);

    private static ApprovalDefinition Chain(int approvals, string kind = ApprovalNodeKinds.Approval)
    {
        var nodes = new List<ApprovalNodeDef> { new("start", ApprovalNodeKinds.Start) };
        nodes.AddRange(Enumerable.Range(1, approvals).Select(i => new ApprovalNodeDef($"a{i}", kind)));
        nodes.Add(new ApprovalNodeDef("end", ApprovalNodeKinds.End));
        var edges = Enumerable.Range(0, nodes.Count - 1).Select(i => new ApprovalEdgeDef($"e{i + 1}", nodes[i].Id, nodes[i + 1].Id)).ToList();
        return Def(nodes, edges);
    }

    private static ApprovalDefinition Sequence(int stages) =>
        new(ApprovalShapes.Sequence, null, null, null,
            [.. Enumerable.Range(1, stages).Select(i => new ApprovalStageDef($"s{i}", new I18nText(), null,
                new ApproverSource(ApprovalSources.Manager, Level: 1), ApprovalModes.Any, [], null, ApprovalOnEmpty.Fallback))]);

    /// <summary>start → fork → n 條分支（各一個簽核）→ join → end。</summary>
    private static ApprovalDefinition Fork(int branches)
    {
        var nodes = new List<ApprovalNodeDef>
        {
            new("start", ApprovalNodeKinds.Start), new("f", ApprovalNodeKinds.Fork),
            new("j", ApprovalNodeKinds.Join, ForkId: "f", Mode: ApprovalModes.All), new("end", ApprovalNodeKinds.End)
        };
        var edges = new List<ApprovalEdgeDef> { new("s1", "start", "f"), new("j1", "j", "end") };
        for (var i = 1; i <= branches; i++)
        {
            nodes.Add(Appr($"b{i}"));
            edges.Add(new($"f-b{i}", "f", $"b{i}"));
            edges.Add(new($"b{i}-j", $"b{i}", "j"));
        }
        return Def(nodes, edges);
    }

    private static List<string> Codes(ApprovalDefinition d, GraphPolicyLimits? l = null, ApprovalDefinition? prev = null) =>
        [.. ApprovalGraphValidator.Check(d, l ?? Policy, prev).Errors.Select(e => e.Code)];

    [Fact]
    public void TooManyNodes_101_Error()
    {
        // 99 個通知節點 + 開始 + 結束 = 101；通知節點不算簽核節點，另一個簽核節點放在鏈上。
        var d = Chain(99, ApprovalNodeKinds.Notify);
        Codes(d).Should().Contain("nodesTooMany");
        Codes(Chain(98, ApprovalNodeKinds.Notify)).Should().NotContain("nodesTooMany");
    }

    [Fact]
    public void TooManyEdges_201_Error()
    {
        var d = Chain(1);
        var edges = d.Edges!.ToList();
        for (var i = edges.Count; i < 201; i++) edges.Add(new ApprovalEdgeDef($"x{i}", "start", "end"));
        Codes(d with { Edges = edges }).Should().Contain("edgesTooMany");
        Codes(d with { Edges = edges.Take(200).ToList() }).Should().NotContain("edgesTooMany");
    }

    private static ApprovalDefinition Nested(int depth)
    {
        // start → f1 → { x1, f2 → { x2, f3 → … } }；每層一個分支節點，最內層兩條分支。
        var nodes = new List<ApprovalNodeDef> { new("start", ApprovalNodeKinds.Start), new("end", ApprovalNodeKinds.End) };
        var edges = new List<ApprovalEdgeDef> { new("s", "start", "f1") };
        var n = 0;
        string E() => $"e{++n}";
        for (var i = 1; i <= depth; i++)
        {
            nodes.Add(new($"f{i}", ApprovalNodeKinds.Fork));
            nodes.Add(new($"j{i}", ApprovalNodeKinds.Join, ForkId: $"f{i}", Mode: ApprovalModes.All));
            nodes.Add(Appr($"x{i}"));
            edges.Add(new(E(), $"f{i}", $"x{i}"));
            edges.Add(new(E(), $"x{i}", $"j{i}"));
            // 外層的另一條分支進入下一層（最內層用簽核節點）
            if (i < depth) { edges.Add(new(E(), $"f{i}", $"f{i + 1}")); edges.Add(new(E(), $"j{i + 1}", $"j{i}")); }
            else { nodes.Add(Appr("y")); edges.Add(new(E(), $"f{i}", "y")); edges.Add(new(E(), "y", $"j{i}")); }
        }
        edges.Add(new(E(), "j1", "end"));
        return Def(nodes, edges);
    }

    [Fact]
    public void Nesting6_Error_Nesting5_Ok()
    {
        var big = new GraphPolicyLimits(50, 5);
        Codes(Nested(5), big).Should().BeEmpty();
        var six = ApprovalGraphValidator.Check(Nested(6), big, null).Errors;
        six.Should().ContainSingle(e => e.Code == "nestingTooDeep" && e.Key == "nodes.f6");
        six.Should().HaveCount(1);
    }

    [Fact]
    public void ApprovalNodes_OverPolicy_ButNotMoreThanPrevious_Ok() =>
        Codes(Chain(3), new GraphPolicyLimits(2, 5), Chain(3)).Should().BeEmpty();

    [Fact]
    public void ApprovalNodes_OverPolicy_MoreThanPrevious_Error() =>
        Codes(Chain(4), new GraphPolicyLimits(2, 5), Chain(3)).Should().Equal("approvalNodesTooMany");

    [Fact]
    public void Branches_OverPolicy_SameForkSameCount_Ok() =>
        Codes(Fork(6), new GraphPolicyLimits(10, 5), Fork(6)).Should().BeEmpty();

    [Fact]
    public void Branches_OverPolicy_NewFork_Error() =>
        Codes(Fork(6), new GraphPolicyLimits(10, 5), Chain(2)).Should().Equal("branchesTooMany");

    [Fact]
    public void Branches_OverPolicy_ForkGrewBeyondPrevious_Error() =>
        Codes(Fork(7), new GraphPolicyLimits(10, 5), Fork(6)).Should().Equal("branchesTooMany");

    [Fact]
    public void PreviousSequence_ConvertedForComparison()
    {
        var limits = new GraphPolicyLimits(10, 5);
        Codes(Chain(12), limits, Sequence(12)).Should().BeEmpty();
        Codes(Chain(13), limits, Sequence(12)).Should().Equal("approvalNodesTooMany");
    }

    [Fact]
    public void NullElements_FailClosed()
    {
        var d = Def([null!, new("start", ApprovalNodeKinds.Start)], [null!]);
        var r = ApprovalGraphValidator.Check(d, Policy, null);
        r.Errors.Should().Contain(e => e.Key == "nodes[0].id" && e.Code == "idInvalid");
        r.Errors.Should().Contain(e => e.Key == "edges[0].id" && e.Code == "idInvalid");
    }

    [Fact]
    public void UnknownKind_Rejected()
    {
        var d = Chain(1);
        d.Nodes!.Add(new("x", "magic"));
        ApprovalGraphValidator.Check(d, Policy, null).Errors.Should().Contain(e => e.Key == "nodes.x" && e.Code == "kindInvalid");
    }
}
