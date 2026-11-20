using System.Text.Json;
using DynamicModules.Domain.Localization;
using FluentAssertions;

namespace DynamicModules.DomainTests.Localization;

/// <summary>介面文字壓平、合併與雜湊（語系包 i4b 計畫判斷 5、6）。</summary>
public class UiTextMergeTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void Flatten_Nested()
    {
        var flat = UiTextMerge.Flatten(Json("""{"a":{"b":"x","c":{"d":"y"}}}"""));

        flat.Should().BeEquivalentTo(new Dictionary<string, string> { ["a.b"] = "x", ["a.c.d"] = "y" });
    }

    [Theory]
    [InlineData("""{"a":["x"]}""")]
    [InlineData("""{"a":{"b":1}}""")]
    [InlineData("""{"a":null}""")]
    [InlineData("""["x"]""")]
    public void Flatten_NotStringOrObject_Throws(string json)
    {
        var act = () => UiTextMerge.Flatten(Json(json));

        act.Should().Throw<FormatException>();
    }
}
