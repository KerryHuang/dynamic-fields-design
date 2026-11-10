using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.DomainTests.Modules;

public class ModuleSectionTests
{
    private static I18nText Make(params (string Locale, string Text)[] items) =>
        new(items.ToDictionary(i => i.Locale, i => i.Text));

    [Fact]
    public void SectionText_OldRowWithoutI18n_IsZhTwOnly()
    {
        var m = new Module { Section = "主檔" };

        m.SectionText()!.Values.Should().Equal(new Dictionary<string, string> { ["zh-TW"] = "主檔" });
    }

    [Fact]
    public void SectionText_NoSection_IsNull() => new Module().SectionText().Should().BeNull();

    [Fact]
    public void SectionText_WithI18n_ReturnsAllLocales()
    {
        var m = new Module();
        m.SetSection(Make(("zh-TW", "主檔"), ("en-US", "Master data")));

        m.Section.Should().Be("主檔");
        m.SectionText()!.Resolve("en-US").Should().Be("Master data");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void SetSection_BlankZhTw_ClearsBoth(string zh)
    {
        var m = new Module();
        m.SetSection(Make(("zh-TW", "主檔"), ("en-US", "Master data")));

        m.SetSection(Make(("zh-TW", zh), ("en-US", "Master data")));

        m.Section.Should().BeNull();
        m.SectionI18n.Should().BeNull();
    }

    [Fact]
    public void SetSection_Null_ClearsBoth()
    {
        var m = new Module();
        m.SetSection(Make(("zh-TW", "主檔")));

        m.SetSection(null);

        m.Section.Should().BeNull();
        m.SectionI18n.Should().BeNull();
    }
}
