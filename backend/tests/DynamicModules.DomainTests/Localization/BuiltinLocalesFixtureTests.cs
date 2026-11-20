using DynamicModules.Domain.Localization;
using FluentAssertions;

namespace DynamicModules.DomainTests.Localization;

/// <summary><see cref="BuiltinLocales"/> 與前端讀的 shared/builtin-locales.json 逐筆逐欄一致（改一邊沒改另一邊就失敗）。</summary>
public class BuiltinLocalesFixtureTests
{
    [Fact]
    public void SameAsSharedFixture()
    {
        var fixture = SharedFile.Load("builtin-locales.json").EnumerateArray()
            .Select(e => new BuiltinLocale(e.GetProperty("code").GetString()!, e.GetProperty("nativeName").GetString()!, e.GetProperty("quasarLang").GetString()!))
            .ToList();

        BuiltinLocales.All.Should().Equal(fixture);
        BuiltinLocales.Codes.Should().Equal(fixture.Select(f => f.Code));
    }

    [Fact]
    public void Codes_AreCanonical_AndContainsWorks()
    {
        BuiltinLocales.Codes.Should().Equal("zh-TW", "en-US", "th", "vi");
        BuiltinLocales.Codes.Should().OnlyContain(c => LocaleCode.IsValid(c));
        BuiltinLocales.Contains("th").Should().BeTrue();
        BuiltinLocales.Contains("ja").Should().BeFalse();
    }
}
