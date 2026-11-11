using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Domain.Policies;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

public class AttachmentRulesTests
{
    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\boot.ini", "boot.ini")]
    [InlineData("C:\\Users\\x\\報價.pdf", "報價.pdf")]
    [InlineData("  spaced.txt  ", "spaced.txt")]
    [InlineData("tab\tname\u0000.txt", "tabname.txt")]
    public void CleanName_StripsPathAndControlChars(string raw, string expected) =>
        AttachmentRules.CleanName(raw).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("dir/")]
    public void CleanName_NothingLeft_ReturnsNull(string? raw) => AttachmentRules.CleanName(raw).Should().BeNull();

    [Fact]
    public void CleanName_LongName_KeepsExtension()
    {
        var name = AttachmentRules.CleanName(new string('x', 400) + ".pdf")!;
        name.Length.Should().BeLessThanOrEqualTo(AttachmentRules.MaxNameLength);
        name.Should().EndWith(".pdf");
    }

    [Theory]
    [InlineData("a.PDF", "pdf")]
    [InlineData("a.tar.gz", "gz")]
    [InlineData("noext", null)]
    [InlineData(".hidden", null)]
    [InlineData("trailing.", null)]
    public void ExtensionOf_LowercaseLastSegment(string name, string? expected) =>
        AttachmentRules.ExtensionOf(name).Should().Be(expected);

    [Theory]
    [InlineData("exe")]
    [InlineData("html")]
    [InlineData("svg")]
    [InlineData("js")]
    [InlineData("sh")]
    [InlineData("zip")]
    public void DefaultWhitelist_DoesNotContainDangerousTypes(string ext) =>
        PolicyCatalog.AttachmentAllowedExtensions.Factory.Split(',').Should().NotContain(ext);
}
