using DynamicModules.Domain.Approvals;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>通知範本語法（流程圖設計 §12.2、計畫 U8）：已知佔位符；其他 {…} 是錯誤；單獨的大括號照字面。</summary>
public class ApprovalNotifyTemplateTests
{
    [Fact]
    public void Parse_KnownPlaceholders()
    {
        var (parts, error) = ApprovalNotifyTemplate.Parse("{title} 的 {flow} 由 {initiator} 在 {node}，金額 {f:21} 元");
        error.Should().BeNull();
        parts.Where(p => p.Placeholder is not null).Select(p => p.Placeholder).Should().Equal("title", "flow", "initiator", "node", "f");
        parts.Single(p => p.FieldId is not null).FieldId.Should().Be(21);
        string.Concat(parts.Select(p => p.Text)).Should().Be(" 的  由  在 ，金額  元");
    }

    [Fact]
    public void Parse_PlainText_SingleTextPart()
    {
        var (parts, error) = ApprovalNotifyTemplate.Parse("申請金額，請備查。");
        error.Should().BeNull();
        parts.Should().ContainSingle().Which.Text.Should().Be("申請金額，請備查。");
    }

    [Fact]
    public void Parse_Empty_NoPartsNoError()
    {
        var (parts, error) = ApprovalNotifyTemplate.Parse("");
        error.Should().BeNull();
        parts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("請 {approver} 確認", "{approver}")]
    [InlineData("{titel}", "{titel}")]
    [InlineData("{ title }", "{ title }")]
    [InlineData("{Title}", "{Title}")]
    [InlineData("a {} b", "{}")]
    public void Parse_UnknownPlaceholder_Error(string text, string expected) =>
        ApprovalNotifyTemplate.Parse(text).Error.Should().Be(expected);

    [Theory]
    [InlineData("收到 :-}")]
    [InlineData("{ 沒有結尾")]
    [InlineData("a { b { c")]
    [InlineData("}{")]
    public void Parse_LoneBraces_Literal(string text)
    {
        var (parts, error) = ApprovalNotifyTemplate.Parse(text);
        error.Should().BeNull();
        string.Concat(parts.Select(p => p.Text)).Should().Be(text);
    }

    [Fact]
    public void Parse_UnclosedThenPlaceholder_PlaceholderStillRecognized()
    {
        var (parts, error) = ApprovalNotifyTemplate.Parse("{ x {title}");
        error.Should().BeNull();
        parts.Select(p => p.Placeholder).Should().Contain("title");
        parts.First().Text.Should().Be("{ x ");
    }

    [Theory]
    [InlineData("{f:abc}")]
    [InlineData("{f:}")]
    [InlineData("{f:-1}")]
    [InlineData("{f:1.5}")]
    [InlineData("{f:99999999999999999999}")]
    public void Parse_FieldIdNotNumber_Error(string text) =>
        ApprovalNotifyTemplate.Parse(text).Error.Should().Be(text);

    [Fact]
    public void Parse_FirstErrorReported() =>
        ApprovalNotifyTemplate.Parse("{a} {b}").Error.Should().Be("{a}");

    [Fact]
    public void FieldIds_AllLocales_FromText() =>
        ApprovalNotifyTemplate.FieldIds("{f:1} 與 {f:2} {title} {f:1} {bad}").Should().BeEquivalentTo([1L, 2L]);
}
