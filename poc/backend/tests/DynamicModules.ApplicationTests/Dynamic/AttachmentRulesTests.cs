using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

public class AttachmentRulesTests
{
    /// <summary>讀值服務直接回指定的白名單（繞過讀值時的 Normalize），用來驗上傳時再擋一次黑名單。</summary>
    private sealed class StubReader(params string[] extensions) : IPolicyReader
    {
        public Task<IReadOnlyList<string>> ListAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(extensions);
        public Task<PolicyResolution> ResolveAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> IntAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> BoolAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> ChoiceAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) => throw new NotSupportedException();
        public void Invalidate() { }
    }

    [Theory]
    [InlineData("exe", false)]
    [InlineData("EXE", false)]
    [InlineData("svg", false)]
    [InlineData("txt", true)]
    [InlineData("TXT", true)]
    [InlineData("zip", false)]
    public async Task IsAllowed_ForbiddenExtensionInList_StillRejected(string ext, bool expected) =>
        (await AttachmentRules.IsAllowedAsync(new StubReader("txt", "exe", "svg"), ext, CancellationToken.None)).Should().Be(expected);

    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\boot.ini", "boot.ini")]
    [InlineData("C:\\Users\\x\\報價.pdf", "報價.pdf")]
    [InlineData("  spaced.txt  ", "spaced.txt")]
    [InlineData("tab\tname\u0000.txt", "tabname.txt")]
    [InlineData("run.EXE\u3000", "run.EXE")]
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
    [InlineData("a.pdf.exe", "exe")]
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
