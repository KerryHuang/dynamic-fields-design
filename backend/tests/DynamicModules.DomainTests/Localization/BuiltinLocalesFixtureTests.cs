using DynamicModules.Domain.Localization;
using FluentAssertions;

namespace DynamicModules.DomainTests.Localization;

/// <summary><see cref="BuiltinLocales"/> 與前端讀的 shared/i18n/&lt;code&gt;/locale.json 逐筆逐欄一致（改一邊沒改另一邊就失敗）。</summary>
public class BuiltinLocalesFixtureTests
{
    [Fact]
    public void SameAsSharedFixture()
    {
        var fixture = BuiltinLocales.Codes
            .Select(code => (Code: code, Json: SharedFile.Load($"i18n/{code}/locale.json")))
            .ToList();

        fixture.Select(f => new BuiltinLocale(f.Json.GetProperty("code").GetString()!, f.Json.GetProperty("nativeName").GetString()!, f.Json.GetProperty("quasarLang").GetString()!))
            .Should().Equal(BuiltinLocales.All);
        fixture.Select(f => f.Json.GetProperty("code").GetString()).Should().Equal(fixture.Select(f => f.Code));
        fixture.Select(f => f.Json.GetProperty("order").GetInt32()).Should().Equal(Enumerable.Range(1, BuiltinLocales.All.Count));
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
