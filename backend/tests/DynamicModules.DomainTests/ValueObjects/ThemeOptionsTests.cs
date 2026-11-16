using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.DomainTests.ValueObjects;

public class ThemeOptionsTests
{
    [Theory]
    [InlineData("workbench")]
    [InlineData("airy")]
    [InlineData("enterprise")]
    [InlineData("industrial")]
    [InlineData("glass")]
    [InlineData("bento")]
    [InlineData("command")]
    [InlineData("botanical")]
    public void NormalizeStyle_AcceptsEveryShippedStyle(string style) =>
        ThemeOptions.NormalizeStyle(style).Should().Be(style);

    [Theory]
    [InlineData("neon")]
    [InlineData("Glass")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeStyle_RejectsUnknownOrDifferentCase(string? style) =>
        ThemeOptions.NormalizeStyle(style).Should().BeNull();
}
