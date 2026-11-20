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

    private static Dictionary<string, string> D(params (string K, string V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);

    private static readonly string[] Base4 = ["k1", "k2", "k3", "k4"];

    [Fact]
    public void Merge_FirstNonEmptyWins()
    {
        var merged = UiTextMerge.Merge(["k"], [D(), D(("k", "")), D(("k", "third"))]);

        merged["k"].Should().Be("third");
    }

    [Fact]
    public void Merge_DropsKeysOutsideBase()
    {
        var merged = UiTextMerge.Merge(["k1"], [D(("k1", "a"), ("extra", "b"))]);

        merged.Keys.Should().Equal("k1");
    }

    [Fact]
    public void Merge_OutputSortedByKey()
    {
        var merged = UiTextMerge.Merge(["b", "a", "c"], [D(("a", "1"), ("b", "2"), ("c", "3"))]);

        merged.Keys.Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Merge_U3_ParentBeforeDefault()
    {
        // ja（無內建層）→ 上層 en-US → 預設 th → zh-TW
        var ja = D(("k1", "ja"));
        var en = D(("k2", "en"));
        var th = D(("k2", "th"), ("k3", "th"));
        var zh = D(("k1", "zh"), ("k2", "zh"), ("k3", "zh"), ("k4", "zh"));

        var merged = UiTextMerge.Merge(Base4, [ja, en, th, zh]);

        merged.Should().Equal(new Dictionary<string, string> { ["k1"] = "ja", ["k2"] = "en", ["k3"] = "th", ["k4"] = "zh" });
    }

    [Fact]
    public void Merge_U3_NoParent_UsesDefault()
    {
        // 設計 U3：ja 沒有上層、預設 th → 缺的句子是泰文，不是繁中
        var th = D(("k1", "th"), ("k2", "th"), ("k3", "th"), ("k4", "th"));
        var zh = D(("k1", "zh"), ("k2", "zh"), ("k3", "zh"), ("k4", "zh"));

        var merged = UiTextMerge.Merge(Base4, [D(("k1", "ja")), th, zh]);

        merged.Values.Should().Equal("ja", "th", "th", "th");
    }

    [Fact]
    public void Serialize_SameContentSameHash()
    {
        var a = UiTextMerge.Serialize(UiTextMerge.Merge(["x", "y"], [D(("y", "2"), ("x", "1"))]));
        var b = UiTextMerge.Serialize(UiTextMerge.Merge(["y", "x"], [D(("x", "1"), ("y", "2"))]));

        a.Hash.Should().Be(b.Hash);
        a.Json.Should().Equal(b.Json);
    }

    [Fact]
    public void Serialize_DifferentContentDifferentHash()
    {
        var a = UiTextMerge.Serialize(UiTextMerge.Merge(["x"], [D(("x", "1"))]));
        var b = UiTextMerge.Serialize(UiTextMerge.Merge(["x"], [D(("x", "2"))]));

        a.Hash.Should().NotBe(b.Hash);
    }

    [Fact]
    public void Serialize_Hash16Hex()
    {
        var (hash, json) = UiTextMerge.Serialize(UiTextMerge.Merge(["menu.home"], [D(("menu.home", "หน้าแรก"))]));

        hash.Should().MatchRegex("^[0-9a-f]{16}$");
        JsonDocument.Parse(json).RootElement.GetProperty("menu.home").GetString().Should().Be("หน้าแรก");
    }

    [Fact]
    public void Serialize_OrdinalOrder()
    {
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // 文化設定的排序會把 a.a 排在 a.B 前；Ordinal 依碼位，大寫在前
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            var merged = UiTextMerge.Merge(["a.a", "a.B"], [D(("a.a", "1"), ("a.B", "2"))]);

            merged.Keys.Should().Equal("a.B", "a.a");
            System.Text.Encoding.UTF8.GetString(UiTextMerge.Serialize(merged).Json).Should().Be("""{"a.B":"2","a.a":"1"}""");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = prev; }
    }
}
