using System.Text.Json;
using DynamicModules.Domain.Approvals;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>流程圖設計器的版面格式（J66）：節點座標，64 KB、±100000、節點代號合法；缺座標允許；不存視角。</summary>
public class ApprovalFlowLayoutTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ApprovalDefinition Def(params string[] nodeIds) =>
        new(ApprovalShapes.Graph, null, null, null, [], [.. nodeIds.Select(id => new ApprovalNodeDef(id, ApprovalNodeKinds.Approval))], []);

    [Fact]
    public void Parse_ValidNodes_ReadsPoints()
    {
        var layout = ApprovalFlowLayout.Parse(J("""{"nodes":{"a":{"x":10.5,"y":-20},"b":{"x":0,"y":8}}}"""));

        layout.Nodes.Should().HaveCount(2);
        layout.Nodes["a"].Should().Be(new LayoutPoint(10.5, -20));
        layout.Nodes["b"].Should().Be(new LayoutPoint(0, 8));
    }

    [Fact]
    public void Parse_Null_IsEmpty()
    {
        ApprovalFlowLayout.Parse((JsonElement?)null).Nodes.Should().BeEmpty();
        ApprovalFlowLayout.Parse(J("null")).Nodes.Should().BeEmpty();
        ApprovalFlowLayout.Parse((string?)null).Nodes.Should().BeEmpty();
        ApprovalFlowLayout.Parse("not json").Nodes.Should().BeEmpty();
    }

    [Fact]
    public void Parse_String_RoundTripsThroughSerialize()
    {
        var layout = ApprovalFlowLayout.Parse(J("""{"nodes":{"a":{"x":1,"y":2}}}"""));

        ApprovalFlowLayout.Parse(layout.ToJson()).Nodes.Should().Equal(layout.Nodes);
    }

    [Fact]
    public void Validate_Null_NoIssues() => ApprovalFlowLayout.Validate(null).Should().BeEmpty();

    [Fact]
    public void Validate_Valid_NoIssues() =>
        ApprovalFlowLayout.Validate(J("""{"nodes":{"a":{"x":100000,"y":-100000}}}""")).Should().BeEmpty();

    [Fact]
    public void Validate_TooLarge_Rejected()
    {
        var pad = new string('a', ApprovalGraphLimits.MaxLayoutBytes);
        var issues = ApprovalFlowLayout.Validate(J($$"""{"nodes":{},"note":"{{pad}}"}"""));

        issues.Should().ContainSingle().Which.Should().Be(new GraphIssue("layout", "layout", "layoutTooLarge"));
    }

    [Theory]
    [InlineData("""{"nodes":{"a":{"x":100001,"y":0}}}""")]
    [InlineData("""{"nodes":{"a":{"x":0,"y":-100000.5}}}""")]
    public void Validate_CoordinateOutOfRange_Rejected(string json) =>
        ApprovalFlowLayout.Validate(J(json)).Should().ContainSingle().Which.Should().Be(new GraphIssue("layout.nodes.a", "layout", "layoutCoordinate"));

    [Theory]
    [InlineData("""{"nodes":{"a":{"x":"1","y":0}}}""")]
    [InlineData("""{"nodes":{"a":{"x":1}}}""")]
    [InlineData("""{"nodes":{"a":[1,2]}}""")]
    [InlineData("""{"nodes":{"a":{"x":1e999,"y":0}}}""")]
    public void Validate_NonFiniteOrNonNumber_Rejected(string json) =>
        ApprovalFlowLayout.Validate(J(json)).Should().ContainSingle().Which.Code.Should().Be("layoutCoordinate");

    [Fact]
    public void Validate_BadNodeCode_Rejected() =>
        ApprovalFlowLayout.Validate(J("""{"nodes":{"A_b":{"x":0,"y":0}}}""")).Should().ContainSingle()
            .Which.Should().Be(new GraphIssue("layout.nodes.A_b", "layout", "layoutNodeCode"));

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"nodes":[]}""")]
    [InlineData("5")]
    public void Validate_WrongShape_Rejected(string json) =>
        ApprovalFlowLayout.Validate(J(json)).Should().ContainSingle().Which.Code.Should().Be("layoutShape");

    [Fact]
    public void Prune_DropsUnknownNodeIds_KeepsMissingAsAbsent()
    {
        var layout = ApprovalFlowLayout.Parse(J("""{"nodes":{"a":{"x":1,"y":2},"gone":{"x":3,"y":4}}}"""));

        var pruned = ApprovalFlowLayout.Prune(layout, Def("a", "b"));

        pruned.Nodes.Keys.Should().Equal("a");
    }
}
