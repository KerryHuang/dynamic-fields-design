using System.Text.Json;
using DynamicModules.Domain.Localization;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.DomainTests.Localization;

/// <summary>讀 shared/locale-chain-cases.json：退回鏈的共用案例，前端 vitest 讀同一份。</summary>
public class LocaleChainTests
{
    private static readonly JsonElement Doc = SharedFile.Load("locale-chain-cases.json");

    private static readonly (string Code, string? FallbackCode)[] Factory =
        [("zh-TW", null), ("en-US", null), ("th", null), ("vi", null)];

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Doc.GetProperty("cases").EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SharedCases(string name)
    {
        var c = Doc.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var installed = c.GetProperty("installed").EnumerateArray()
            .Select(i => (i.GetProperty("code").GetString()!, i.GetProperty("fallback").ValueKind == JsonValueKind.Null ? null : i.GetProperty("fallback").GetString()))
            .ToList();
        var locale = c.GetProperty("locale").GetString()!;
        var fallbacks = LocaleChain.Fallbacks(locale, c.GetProperty("default").GetString()!, installed);

        fallbacks.Should().Equal(c.GetProperty("fallbacks").EnumerateArray().Select(f => f.GetString()!), name);
        var text = new I18nText(c.GetProperty("text").EnumerateObject().Select(p => KeyValuePair.Create(p.Name, p.Value.GetString()!)));
        text.Resolve(locale, fallbacks).Should().Be(c.GetProperty("expected").GetString(), name);
    }

    [Fact]
    public void Factory_SameAsP3()
    {
        LocaleChain.Fallbacks("th", "zh-TW", Factory).Should().Equal("zh-TW", "en-US", "th", "vi");
        LocaleChain.Fallbacks("th", "en-US", Factory).Should().Equal("en-US", "zh-TW", "th", "vi");
        var text = new I18nText([KeyValuePair.Create("zh-TW", "訂單"), KeyValuePair.Create("en-US", "Order")]);
        text.Resolve("th", LocaleChain.Fallbacks("th", "en-US", Factory)).Should().Be(text.Resolve("th", ["en-US", "zh-TW"]));
    }

    [Fact]
    public void UninstalledDefaultAndBaseline_StillListed()
    {
        LocaleChain.Fallbacks("ja", "ko", [("en-US", null)]).Should().Equal("ko", "zh-TW", "en-US");
    }
}
