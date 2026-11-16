using System.Text.Json;
using DynamicModules.Domain.Approvals;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>草稿的存放格式（J65）：{ rev, definition, layout }；讀不懂一律回 null，不丟例外。</summary>
public class ApprovalFlowDraftTests
{
    private static ApprovalDefinition Def() =>
        new(ApprovalShapes.Graph, null, null, null, [],
            [new ApprovalNodeDef("start", ApprovalNodeKinds.Start), new ApprovalNodeDef("end", ApprovalNodeKinds.End)],
            [new ApprovalEdgeDef("e1", "start", "end")]);

    [Fact]
    public void TryParse_RoundTrip()
    {
        var layout = ApprovalFlowLayout.Parse("""{"nodes":{"start":{"x":0,"y":0}}}""");
        var draft = new ApprovalFlowDraft("abc123", Def(), layout);

        var back = ApprovalFlowDraft.TryParse(draft.Serialize());

        back.Should().NotBeNull();
        back!.Rev.Should().Be("abc123");
        ApprovalDefinitionJson.SameAs(back.Definition, Def()).Should().BeTrue();
        back.Layout!.Nodes.Should().Equal(layout.Nodes);
    }

    [Fact]
    public void TryParse_NullLayout_StaysNull()
    {
        var back = ApprovalFlowDraft.TryParse(new ApprovalFlowDraft("r", Def(), null).Serialize());

        back!.Layout.Should().BeNull();
    }

    [Fact]
    public void Serialize_ShapeIsRevDefinitionLayout()
    {
        using var doc = JsonDocument.Parse(new ApprovalFlowDraft("r1", Def(), null).Serialize());

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("rev", "definition", "layout");
        doc.RootElement.GetProperty("definition").GetProperty("shape").GetString().Should().Be("graph");
    }

    [Theory]
    [InlineData("""{"shape":"graph"}""")]
    [InlineData("""{"shape":"graph","nodes":[],"edges":[]}""")]
    public void TryParse_OldShapeWithoutRev_ReturnsNull(string old) => ApprovalFlowDraft.TryParse(old).Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public void TryParse_BrokenJson_ReturnsNull(string? json) => ApprovalFlowDraft.TryParse(json).Should().BeNull();

    [Theory]
    [InlineData("""{"rev":"r","definition":"oops"}""")]
    [InlineData("""{"rev":"r","definition":null}""")]
    [InlineData("""{"rev":"r","definition":{"shape":5}}""")]
    [InlineData("""{"rev":"r"}""")]
    [InlineData("""{"rev":"","definition":{"shape":"graph"}}""")]
    public void TryParse_DefinitionNotDeserializable_ReturnsNull(string json) => ApprovalFlowDraft.TryParse(json).Should().BeNull();

    [Fact]
    public void NewRev_IsUniquePerCall()
    {
        var revs = Enumerable.Range(0, 100).Select(_ => ApprovalFlowDraft.NewRev()).ToList();

        revs.Distinct().Should().HaveCount(100);
        revs.Should().OnlyContain(r => r.Length == 32 && r.All(char.IsAsciiHexDigitLower));
    }

    [Fact]
    public void TokenOnly_HasToken_ButNoDraft()
    {
        var json = ApprovalFlowDraft.TokenOnly("tok1");

        ApprovalFlowDraft.TokenOf(json).Should().Be("tok1");
        ApprovalFlowDraft.TryParse(json).Should().BeNull("只有權杖不算有草稿");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"definition":{"shape":"graph"}}""")]
    [InlineData("""{"rev":""}""")]
    [InlineData("""{"rev":5}""")]
    [InlineData("[]")]
    public void TokenOf_NoReadableRev_ReturnsNull(string? json) => ApprovalFlowDraft.TokenOf(json).Should().BeNull();

    [Fact]
    public void TokenOf_FullDraft_ReturnsRev() =>
        ApprovalFlowDraft.TokenOf(new ApprovalFlowDraft("abc", Def(), null).Serialize()).Should().Be("abc");

    [Fact]
    public void WithNewRev_KeepsDraftContent_OrLeavesTokenOnly()
    {
        var full = new ApprovalFlowDraft("old", Def(), ApprovalFlowLayout.Parse("""{"nodes":{"start":{"x":1,"y":2}}}""")).Serialize();

        var rotated = ApprovalFlowDraft.TryParse(ApprovalFlowDraft.WithNewRev(full))!;
        var fromNothing = ApprovalFlowDraft.WithNewRev(null);

        rotated.Rev.Should().NotBe("old");
        ApprovalDefinitionJson.SameAs(rotated.Definition, Def()).Should().BeTrue();
        rotated.Layout!.Nodes["start"].Should().Be(new LayoutPoint(1, 2));
        ApprovalFlowDraft.TokenOf(fromNothing).Should().NotBeNullOrEmpty();
        ApprovalFlowDraft.TryParse(fromNothing).Should().BeNull();
    }
}
