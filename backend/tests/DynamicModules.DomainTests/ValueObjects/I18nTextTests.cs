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
    public void IsValid_EmptyZhTw_ReturnsFalse()
    {
        Make(("zh-TW", " ")).IsValid(out var error).Should().BeFalse();
        error.Should().NotBeNull();
        new I18nText().IsValid(out _).Should().BeFalse();
    }

    [Fact]
    public void IsValid_UnknownLocale_ReturnsFalse()
    {
        Make(("zh-TW", "a"), ("ja", "b")).IsValid(out var error).Should().BeFalse();
        error.Should().Contain("ja");
    }

    [Fact]
    public void IsValid_SupportedLocales_ReturnsTrue()
    {
        Make(("zh-TW", "a"), ("en-US", "b"), ("th", "c"), ("vi", "d")).IsValid(out var error).Should().BeTrue();
        error.Should().BeNull();
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
