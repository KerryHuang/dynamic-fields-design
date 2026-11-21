using DynamicModules.Domain.Mcp;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Mcp;

public class McpSecretsTests
{
    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    [Fact]
    public void NewToken_PrefixFixedLengthBase62_Unique()
    {
        var tokens = Enumerable.Range(0, 1000).Select(_ => McpSecrets.NewToken()).ToList();
        tokens.Should().OnlyContain(t => t.StartsWith("fwp_"));
        tokens.Select(t => t.Length).Distinct().Should().ContainSingle().Which.Should().Be(McpSecrets.TokenLength);
        tokens.Select(t => t.Substring(4)).Should().OnlyContain(r => r.All(c => Base62.Contains(c)));
        tokens.Distinct().Should().HaveCount(1000);
        tokens.Should().OnlyContain(t => McpSecrets.LooksLikeToken(t));
    }

    [Fact]
    public void NewConfirmationCode_PrefixAndBase62()
    {
        var code = McpSecrets.NewConfirmationCode();
        code.Should().StartWith("fwc_");
        code[4..].Should().HaveLength(22).And.Match(s => s.All(c => Base62.Contains(c)));
        McpSecrets.LooksLikeToken(code).Should().BeFalse();
    }

    [Fact]
    public void Hash_Is64LowerHex_Deterministic()
    {
        var h = McpSecrets.Hash("fwp_abc");
        h.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
        McpSecrets.Hash("fwp_abc").Should().Be(h);
        McpSecrets.Hash("fwp_abd").Should().NotBe(h);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fwx_0123456789012345678901234567890123456789012")]
    [InlineData("fwp_012345678901234567890123456789012345678901")]   // 少一個字
    [InlineData("fwp_01234567890123456789012345678901234567890123")] // 多一個字
    [InlineData("fwp_0123456789012345678901 34567890123456789012")]  // 含空白
    [InlineData("fwp_0123456789012345678901-34567890123456789012")]  // 非 base62
    public void LooksLikeToken_Rejects(string s) => McpSecrets.LooksLikeToken(s).Should().BeFalse();

    [Fact]
    public void LooksLikeToken_AcceptsWellFormed() =>
        McpSecrets.LooksLikeToken("fwp_0123456789012345678901234567890123456789012").Should().BeTrue();

    [Fact]
    public void HashEquals_SameTrue_OneCharOffFalse_LengthDiffFalse()
    {
        var h = McpSecrets.Hash("x");
        McpSecrets.HashEquals(h, McpSecrets.Hash("x")).Should().BeTrue();
        var off = (h[0] == 'a' ? 'b' : 'a') + h[1..];
        McpSecrets.HashEquals(h, off).Should().BeFalse();
        McpSecrets.HashEquals(h, h[..63]).Should().BeFalse();
    }

    [Fact]
    public void Last4_IsLastFourChars() =>
        McpSecrets.Last4("fwp_0123456789012345678901234567890123456789012").Should().Be("9012");
}
