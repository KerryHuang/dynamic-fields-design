using DynamicModules.Domain.Mcp;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Mcp;

public class McpScopesTests
{
    [Fact]
    public void All_SixInFixedOrder() =>
        McpScopes.All.Should().Equal("business.read", "business.write", "design.read", "design.write", "ops.read", "ops.act");

    [Fact]
    public void Issuable_BusinessReadAndWrite() => McpScopes.Issuable.Should().Equal("business.read", "business.write");

    [Fact]
    public void DefaultChecked_ReadOnly_SubsetOfIssuable()
    {
        McpScopes.DefaultChecked.Should().Equal("business.read");
        McpScopes.Issuable.Should().Contain(McpScopes.DefaultChecked);
    }

    [Theory]
    [InlineData("business.read,unknown")]
    [InlineData("")]
    [InlineData("business.read,,design.read")]
    [InlineData("Business.Read")]
    public void TryParse_UnknownOrEmpty_False(string csv) => McpScopes.TryParse(csv, out _).Should().BeFalse();

    [Fact]
    public void TryParse_DedupesAndOrdersByAll()
    {
        McpScopes.TryParse("design.read,business.read,design.read", out var scopes).Should().BeTrue();
        scopes.Should().Equal("business.read", "design.read");
    }

    [Fact]
    public void Join_RoundTrips()
    {
        var csv = McpScopes.Join(["ops.read", "business.read"]);
        csv.Should().Be("business.read,ops.read");
        McpScopes.TryParse(csv, out var back).Should().BeTrue();
        back.Should().Equal("business.read", "ops.read");
    }
}
