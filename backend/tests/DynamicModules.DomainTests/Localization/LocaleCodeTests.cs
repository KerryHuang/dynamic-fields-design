using DynamicModules.Domain.Localization;
using FluentAssertions;

namespace DynamicModules.DomainTests.Localization;

public class LocaleCodeTests
{
    [Theory]
    [InlineData("ja")]
    [InlineData("zh-TW")]
    [InlineData("zh-Hant-HK")]
    [InlineData("es-419")]
    [InlineData("fil")]
    public void Canonical_IsValid(string code) => LocaleCode.IsValid(code).Should().BeTrue();

    [Theory]
    [InlineData("JA")]
    [InlineData("zh_TW")]
    [InlineData("e")]
    [InlineData("abcd")]
    [InlineData("zh-tw")]
    [InlineData("zh-Hant-hk")]
    [InlineData("")]
    [InlineData(null)]
    public void NonCanonical_IsInvalid(string? code) => LocaleCode.IsValid(code).Should().BeFalse();

    [Fact]
    public void TooLong_IsInvalid() => LocaleCode.IsValid(new string('a', LocaleCode.MaxLength + 1)).Should().BeFalse();

    [Theory]
    [InlineData("JA", "ja")]
    [InlineData("zh-hant-hk", "zh-Hant-HK")]
    [InlineData("es-419", "es-419")]
    [InlineData("zh-TW", "zh-TW")]
    public void TryNormalize_Canonicalizes(string raw, string expected)
    {
        LocaleCode.TryNormalize(raw, out var code).Should().BeTrue();
        code.Should().Be(expected);
    }

    [Theory]
    [InlineData("zh_TW")]
    [InlineData("e")]
    [InlineData("")]
    [InlineData(null)]
    public void TryNormalize_RejectsMalformed(string? raw) => LocaleCode.TryNormalize(raw, out _).Should().BeFalse();
}
