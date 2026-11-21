using System.Text.Json;
using DynamicModules.Domain.Mcp;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Mcp;

public class McpArgumentsTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void KeyOrder_DoesNotMatter() =>
        McpArguments.Hash(J("""{"b":1,"a":{"y":2,"x":[3,{"q":1,"p":2}]}}"""))
            .Should().Be(McpArguments.Hash(J("""{ "a": { "x": [3, {"p":2,"q":1}], "y": 2 }, "b": 1 }""")));

    [Fact]
    public void Canonical_SortsKeysAndRemovesWhitespace() =>
        McpArguments.Canonical(J("""{ "b": "s", "a": [1, true, null] }""")).Should().Be("""{"a":[1,true,null],"b":"s"}""");

    [Fact]
    public void TopLevelConfirmationCode_IsIgnored() =>
        McpArguments.Hash(J("""{"a":1,"confirmationCode":"fwc_x"}""")).Should().Be(McpArguments.Hash(J("""{"a":1}""")));

    [Fact]
    public void NestedConfirmationCode_IsKept() =>
        McpArguments.Hash(J("""{"a":{"confirmationCode":"x"}}""")).Should().NotBe(McpArguments.Hash(J("""{"a":{}}""")));

    [Fact]
    public void DifferentValue_DifferentHash()
    {
        McpArguments.Hash(J("""{"a":1}""")).Should().NotBe(McpArguments.Hash(J("""{"a":2}""")));
        McpArguments.Hash(J("""{"a":{"b":"x"}}""")).Should().NotBe(McpArguments.Hash(J("""{"a":{"b":"y"}}""")));
    }

    /// <summary>數字照 JSON 原字，不正規化：<c>1</c> 與 <c>1.0</c> 是不同參數（確認碼要求參數一字不差）。</summary>
    [Fact]
    public void Numbers_KeepRawText_NotNormalized() =>
        McpArguments.Hash(J("""{"a":1}""")).Should().NotBe(McpArguments.Hash(J("""{"a":1.0}""")));

    [Fact]
    public void Hash_Is64LowerHex() => McpArguments.Hash(J("{}")).Should().MatchRegex("^[0-9a-f]{64}$");

    [Fact]
    public void Undefined_IsEmptyObject() => McpArguments.Canonical(default).Should().Be("{}");
}
