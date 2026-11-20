using DynamicModules.Domain.Policies;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Policies;

/// <summary>語系三政策的互相約束（政策設計 p3 計畫判斷 10、Task 8）。</summary>
public class LocalePolicyRulesTests
{
    private static readonly string[] Installed = ["zh-TW", "en-US", "th", "vi"];

    [Fact]
    public void Factory_Ok() =>
        LocalePolicyRules.Check("zh-TW", ["zh-TW", "en-US", "th", "vi"], ["zh-TW"], Installed).Should().BeNull();

    [Fact]
    public void NotInstalled() =>
        LocalePolicyRules.Check("zh-TW", ["zh-TW", "ja"], ["zh-TW"], Installed).Should().Be(LocalePolicyRules.NotInstalled);

    [Fact]
    public void EnabledEmpty() =>
        LocalePolicyRules.Check("zh-TW", [], ["zh-TW"], Installed).Should().Be(LocalePolicyRules.EnabledEmpty);

    [Fact]
    public void DefaultNotEnabled() =>
        LocalePolicyRules.Check("zh-TW", ["en-US"], ["en-US"], Installed).Should().Be(LocalePolicyRules.DefaultNotEnabled);

    [Fact]
    public void RequiredEmpty() =>
        LocalePolicyRules.Check("zh-TW", ["zh-TW"], [], Installed).Should().Be(LocalePolicyRules.RequiredEmpty);

    [Fact]
    public void RequiredNotEnabled() =>
        LocalePolicyRules.Check("zh-TW", ["zh-TW", "th"], ["zh-TW", "en-US"], Installed).Should().Be(LocalePolicyRules.RequiredNotEnabled);
}
