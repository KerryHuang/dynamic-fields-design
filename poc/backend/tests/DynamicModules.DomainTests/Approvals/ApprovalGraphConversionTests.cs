using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>sequence → 直線流程圖（流程圖設計 §7.8）：無損、可重複執行、結果穩定。</summary>
public class ApprovalGraphConversionTests
{
    private static I18nText T(string s) => new([new("zh-TW", s)]);

    private static ApprovalStageDef Stage(string code, JsonElement? when = null, ApproverSource? approvers = null, List<long>? editable = null, int? due = null) =>
        new(code, T(code), when, approvers ?? new ApproverSource(ApprovalSources.Manager, Level: 1), ApprovalModes.Any, editable ?? [], due, ApprovalOnEmpty.Fallback);

    private static ApprovalDefinition Seq(params ApprovalStageDef[] stages) =>
        new(ApprovalShapes.Sequence, GraphSamples.Cond(1), 11, new ApproverSource(ApprovalSources.Group, GroupIds: [7]), [.. stages]);

    [Fact]
    public void Convert_ThreeStages_LinearGraph()
    {
        var d = Seq(Stage("a"), Stage("b"), Stage("c"));
        var g = ApprovalGraphConversion.FromSequence(d);

        g.Shape.Should().Be(ApprovalShapes.Graph);
        g.Stages.Should().BeEmpty();
        g.Nodes!.Select(n => (n.Id, n.Kind)).Should().Equal(
            ("start", "start"), ("a", "approval"), ("b", "approval"), ("c", "approval"), ("end", "end"));
        g.Edges!.Select(e => (e.Id, e.From, e.To)).Should().Equal(
            ("e1", "start", "a"), ("e2", "a", "b"), ("e3", "b", "c"), ("e4", "c", "end"));
        g.Edges!.Should().OnlyContain(e => e.When == null && e.Order == null && e.Default == null && e.Label == null);
        g.When!.Value.GetRawText().Should().Be(d.When!.Value.GetRawText());
        g.RejectTo.Should().Be(11);
        g.FallbackApprovers.Should().BeSameAs(d.FallbackApprovers);
    }

    [Fact]
    public void Convert_NoStages_StartToEnd()
    {
        var g = ApprovalGraphConversion.FromSequence(Seq());
        g.Nodes!.Select(n => n.Id).Should().Equal("start", "end");
        g.Edges!.Should().ContainSingle().Which.Should().Be(new ApprovalEdgeDef("e1", "start", "end"));
    }

    [Fact]
    public void Convert_KeepsStageAttributesIncludingUnmatchedMarks()
    {
        var approvers = new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [3], Min: 1, Max: 2,
            UnmatchedGroups: ["legacy"], CandidatesUnmatched: true);
        var s = Stage("pick", GraphSamples.Cond(5), approvers, [8, 9], 48) with { Mode = ApprovalModes.All, OnEmpty = ApprovalOnEmpty.Skip };
        var node = ApprovalGraphConversion.FromSequence(Seq(s)).Nodes!.Single(n => n.Id == "pick");

        node.Kind.Should().Be(ApprovalNodeKinds.Approval);
        node.Name.Should().BeSameAs(s.Name);
        node.When!.Value.GetRawText().Should().Be(s.When!.Value.GetRawText());
        node.Approvers.Should().BeSameAs(approvers);
        node.Approvers!.UnmatchedGroups.Should().Equal("legacy");
        node.Approvers.CandidatesUnmatched.Should().BeTrue();
        node.Mode.Should().Be(ApprovalModes.All);
        node.EditableFields.Should().Equal(8, 9);
        node.DueHours.Should().Be(48);
        node.OnEmpty.Should().Be(ApprovalOnEmpty.Skip);
        node.AsStage().Should().Be(s);
    }

    [Fact]
    public void Convert_StageCodedStart_UsesStart0()
    {
        var g = ApprovalGraphConversion.FromSequence(Seq(Stage("start"), Stage("b")));
        g.Nodes!.Select(n => n.Id).Should().Equal("start-0", "start", "b", "end");
        g.Edges!.First().Should().Be(new ApprovalEdgeDef("e1", "start-0", "start"));
    }

    [Fact]
    public void Convert_Start0AlsoTaken_UsesStart1()
    {
        var g = ApprovalGraphConversion.FromSequence(Seq(Stage("start"), Stage("start-0")));
        g.Nodes!.First().Id.Should().Be("start-1");
    }

    [Fact]
    public void Convert_StageCodedEnd_UsesEnd0()
    {
        var g = ApprovalGraphConversion.FromSequence(Seq(Stage("a"), Stage("end")));
        g.Nodes!.Select(n => n.Id).Should().Equal("start", "a", "end", "end-0");
        g.Edges!.Last().Should().Be(new ApprovalEdgeDef("e3", "end", "end-0"));
    }

    [Fact]
    public void Convert_StageCodedE1_EdgeIdsUnaffected()
    {
        var g = ApprovalGraphConversion.FromSequence(Seq(Stage("e1"), Stage("e2")));
        g.Nodes!.Select(n => n.Id).Should().Equal("start", "e1", "e2", "end");
        g.Edges!.Select(e => e.Id).Should().Equal("e1", "e2", "e3");
    }

    [Fact]
    public void Convert_Twice_SameResult()
    {
        var d = Seq(Stage("a"), Stage("b"));
        ApprovalDefinitionJson.SameAs(ApprovalGraphConversion.FromSequence(d), ApprovalGraphConversion.FromSequence(d)).Should().BeTrue();
    }

    [Fact]
    public void Convert_Graph_ReturnsSameInstance()
    {
        var g = ApprovalGraphConversion.FromSequence(Seq(Stage("a")));
        ApprovalGraphConversion.FromSequence(g).Should().BeSameAs(g);
        var sample = GraphSamples.Graph();
        ApprovalGraphConversion.FromSequence(sample).Should().BeSameAs(sample);
    }

    [Fact]
    public void Convert_DoesNotMutateInput()
    {
        var d = Seq(Stage("a"));
        var before = JsonSerializer.Serialize(d);
        ApprovalGraphConversion.FromSequence(d);
        JsonSerializer.Serialize(d).Should().Be(before);
        d.Nodes.Should().BeNull();
    }

    [Fact]
    public void Convert_RefsUnchanged()
    {
        var d = Seq(Stage("a", GraphSamples.Cond(2), new ApproverSource(ApprovalSources.UserField, FieldId: 77, CandidateGroupIds: [3]), [8]),
            Stage("b", approvers: new ApproverSource(ApprovalSources.Group, GroupIds: [4])));
        var g = ApprovalGraphConversion.FromSequence(d);

        FieldClosure.DirectFields(g).Should().BeEquivalentTo(FieldClosure.DirectFields(d));
        FieldClosure.ReferencedFields(g).Should().BeEquivalentTo(FieldClosure.ReferencedFields(d));
        ApprovalDefinitionRefs.GroupRefs(g).Should().BeEquivalentTo(ApprovalDefinitionRefs.GroupRefs(d));
    }
}
