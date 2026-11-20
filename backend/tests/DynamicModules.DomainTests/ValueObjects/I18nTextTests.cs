using System.Text.Json;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.DomainTests.ValueObjects;

public class I18nTextTests
{
    private static I18nText Make(params (string Locale, string Text)[] items) =>
        new(items.ToDictionary(i => i.Locale, i => i.Text));

    [Fact]
    public void Resolve_MissingLocale_FallsBackToZhTw() =>
        Make(("zh-TW", "訂單")).Resolve("vi").Should().Be("訂單");

    [Fact]
    public void Resolve_EmptyString_FallsBackToZhTw() =>
        Make(("zh-TW", "訂單"), ("en-US", "")).Resolve("en-US").Should().Be("訂單");

    [Fact]
    public void Resolve_ExistingLocale_ReturnsIt() =>
        Make(("zh-TW", "訂單"), ("en-US", "Order")).Resolve("en-US").Should().Be("Order");

    [Fact]
    public void IsWellFormed_NeedsSomeTextAndKnownLocales()
    {
        new I18nText().IsWellFormed(out var empty).Should().BeFalse();
        empty.Should().Be("至少要有一種語系的文字");
        Make(("zh-TW", " ")).IsWellFormed(out _).Should().BeFalse();
        Make(("en-US", "Order")).IsWellFormed(out var ok).Should().BeTrue();
        ok.Should().BeNull();
        Make(("zh-TW", "a"), ("en-US", "b"), ("th", "c"), ("vi", "d")).IsWellFormed(out _).Should().BeTrue();
        // M1：只驗代碼格式，沒安裝的語系也收
        Make(("ja", "受注")).IsWellFormed(out _).Should().BeTrue();
        Make(("jp", "x")).IsWellFormed(out _).Should().BeTrue();
        Make(("JP", "x")).IsWellFormed(out var invalid).Should().BeFalse();
        invalid.Should().Be("不支援的語系：JP");
        Make(("zh_TW", "x")).IsWellFormed(out _).Should().BeFalse();
        I18nText.InvalidLocale(["zh-TW", "ja", "e"]).Should().Be("e");
        I18nText.InvalidLocale(["zh-TW", "ja"]).Should().BeNull();
    }

    [Fact]
    public void Resolve_SafetyNet_OnlyBuiltinLocales()
    {
        // 安全網只看內建語系：只有沒安裝的語系有文字時回空字串（U6）
        Make(("ko", "x")).Resolve("vi").Should().Be("");
        Make(("vi", "Đơn")).Resolve("th").Should().Be("Đơn");
    }

    [Fact]
    public void Resolve_FallbackChain()
    {
        // 判斷 13：要求的語系 → 退回鏈（沒傳時是繁中）→ 支援語系順序中第一個有值的
        Make(("en-US", "Order")).Resolve("th").Should().Be("Order");
        var both = Make(("zh-TW", "訂單"), ("en-US", "Order"));
        both.Resolve("th", ["en-US", "zh-TW"]).Should().Be("Order");
        both.Resolve("th").Should().Be("訂單");
        Make(("vi", "Đơn"), ("th", "ใบ")).Resolve("en-US", ["zh-TW"]).Should().Be("ใบ");
        new I18nText().Resolve("th").Should().Be("");
    }

    [Fact]
    public void Resolve_LocaleContext_SameAsTwoArgs()
    {
        var t = Make(("zh-TW", "訂單"), ("en-US", "Order"));
        t.Resolve(new LocaleContext("th", ["en-US", "zh-TW"])).Should().Be(t.Resolve("th", ["en-US", "zh-TW"])).And.Be("Order");
    }

    [Fact]
    public void HasText_MissingLocales_SameAs()
    {
        var t = Make(("zh-TW", "訂單"), ("en-US", " "));
        t.HasText("zh-TW").Should().BeTrue();
        t.HasText("en-US").Should().BeFalse();
        t.MissingLocales(["zh-TW", "en-US", "th"]).Should().Equal("en-US", "th");
        t.SameAs(Make(("zh-TW", " 訂單 "))).Should().BeTrue();
        t.SameAs(Make(("zh-TW", "訂單"), ("th", "ใบ"))).Should().BeFalse();
        t.SameAs(Make(("zh-TW", "訂單2"))).Should().BeFalse();
        t.SameAs(null).Should().BeFalse();
    }

    [Fact]
    public void Json_IsFlatObjectKeyedByLocale()
    {
        var json = JsonSerializer.Serialize(Make(("zh-TW", "訂單"), ("en-US", "Order")));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("zh-TW").GetString().Should().Be("訂單");
        doc.RootElement.GetProperty("en-US").GetString().Should().Be("Order");
        doc.RootElement.EnumerateObject().Should().HaveCount(2);

        var back = JsonSerializer.Deserialize<I18nText>("""{"zh-TW":"訂單","en-US":"Order"}""")!;
        back.Values.Should().BeEquivalentTo(new Dictionary<string, string> { ["zh-TW"] = "訂單", ["en-US"] = "Order" });
    }
}
