using DynamicModules.Domain.Policies;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Policies;

public class PolicyDefinitionTests
{
    private static PolicyDefinition Int() => new("t.int", "task", PolicyKind.Integer, PolicyScope.Module, "20", 1, 100);
    private static PolicyDefinition Bool() => new("t.bool", "task", PolicyKind.Boolean, PolicyScope.Module, "true");
    private static PolicyDefinition Choice() => new("t.choice", "locale", PolicyKind.Choice, PolicyScope.System, "monday", Options: ["monday", "sunday"]);
    private static PolicyDefinition Roles() => new("t.roles", "task", PolicyKind.RoleSet, PolicyScope.Module, "assignee,creator", Options: ["assignee", "creator"]);
    private static PolicyDefinition Exts() => new("t.ext", "attachment", PolicyKind.CodeList, PolicyScope.System, "pdf", 1, 40);

    private static string FortyOne() => string.Join(",", Enumerable.Range(1, 41).Select(i => $"e{i}"));

    [Theory]
    [InlineData("20", "20")]
    [InlineData("1", "1")]
    [InlineData("100", "100")]
    [InlineData("0", null)]
    [InlineData("101", null)]
    [InlineData("abc", null)]
    [InlineData("2.5", null)]
    [InlineData("+5", null)]
    [InlineData(" 5", null)]
    [InlineData(null, null)]
    public void Integer_Normalize(string? raw, string? expected) => Int().Normalize(raw).Should().Be(expected);

    [Theory]
    [InlineData("true", "true")]
    [InlineData("TRUE", "true")]
    [InlineData("False", "false")]
    [InlineData("no", null)]
    [InlineData(null, null)]
    public void Boolean_Normalize(string? raw, string? expected) => Bool().Normalize(raw).Should().Be(expected);

    [Theory]
    [InlineData("monday", "monday")]
    [InlineData("sunday", "sunday")]
    [InlineData("Monday", null)]
    [InlineData("", null)]
    public void Choice_Normalize(string? raw, string? expected) => Choice().Normalize(raw).Should().Be(expected);

    [Theory]
    [InlineData("creator,assignee", "assignee,creator")]
    [InlineData("", "")]
    [InlineData("creator,creator", "creator")]
    [InlineData("owner", null)]
    [InlineData(null, null)]
    public void RoleSet_Normalize(string? raw, string? expected) => Roles().Normalize(raw).Should().Be(expected);

    [Theory]
    [InlineData(".PDF, png,pdf", "pdf,png")]
    [InlineData("", null)]
    [InlineData("p-d", null)]
    [InlineData(null, null)]
    public void CodeList_Normalize(string? raw, string? expected) => Exts().Normalize(raw).Should().Be(expected);

    [Fact]
    public void CodeList_OverMax_Rejected() => Exts().Normalize(FortyOne()).Should().BeNull();

    [Theory]
    [InlineData("0", "1")]
    [InlineData("999", "100")]
    [InlineData("50", "50")]
    [InlineData("x", null)]
    public void Integer_Clamp(string? raw, string? expected) => Int().Clamp(raw).Should().Be(expected);

    [Fact]
    public void CodeList_Clamp_DropsBadItems_AndTruncates()
    {
        Exts().Clamp("pdf,bad-ext").Should().Be("pdf");
        Exts().Clamp("bad-ext").Should().BeNull();
        Exts().Clamp(FortyOne()).Should().Be(string.Join(",", Enumerable.Range(1, 40).Select(i => $"e{i}")));
    }

    [Theory]
    [InlineData("pdf,exe")]
    [InlineData("HTML")]
    [InlineData(".svg,png")]
    public void Normalize_ForbiddenExtension_Rejected(string raw)
    {
        PolicyCatalog.AttachmentAllowedExtensions.Normalize(raw).Should().BeNull();
        PolicyCatalog.AttachmentAllowedExtensions.ForbiddenIn(raw).Should().NotBeEmpty();
    }

    [Fact]
    public void Clamp_ForbiddenExtension_Dropped()
    {
        // 部署設定檔：黑名單項目丟掉；Clamp 本身不記 log，Warning 由 PolicyEnvironmentDefaults.Build 記（見 Clamp_ConfigForbiddenExtension_DroppedWithWarning）
        PolicyCatalog.AttachmentAllowedExtensions.Clamp("pdf,exe,.SVG,png").Should().Be("pdf,png");
        PolicyCatalog.AttachmentAllowedExtensions.Clamp("exe,html").Should().BeNull();
    }

    [Fact]
    public void Forbidden_ListIsNotInFactory_AndMatchesLowercaseItems()
    {
        var forbidden = PolicyCatalog.AttachmentAllowedExtensions.Forbidden!;
        forbidden.Should().Contain(["html", "svg", "exe", "js", "sh", "php"]);
        PolicyCatalog.AttachmentAllowedExtensions.Factory.Split(',').Should().NotIntersectWith(forbidden);
        PolicyCatalog.AttachmentAllowedExtensions.IsForbidden("EXE").Should().BeTrue();
        PolicyCatalog.AttachmentAllowedExtensions.IsForbidden("pdf").Should().BeFalse();
    }

    [Fact]
    public void Clamp_OtherKinds_SameAsNormalize()
    {
        Bool().Clamp("TRUE").Should().Be("true");
        Roles().Clamp("owner").Should().BeNull();
    }

    [Fact]
    public void SettingKey_HasPolicyPrefix() => Int().SettingKey.Should().Be("policy.t.int");
}
