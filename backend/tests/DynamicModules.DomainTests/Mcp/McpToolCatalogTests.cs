using DynamicModules.Domain.Mcp;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Mcp;

public class McpToolCatalogTests
{
    [Fact]
    public void Production_TwelveTools_IncludingWhoami() =>
        McpToolCatalog.Production.Select(t => t.Name).Should().BeEquivalentTo(
            "whoami", "list_modules", "describe_module", "query_records", "get_record", "get_record_history",
            "list_reports", "run_report", "list_my_tasks", "get_task", "list_approval_tasks", "get_record_approval");

    [Fact]
    public void Names_UniqueSnakeCase()
    {
        var names = McpToolCatalog.Production.Select(t => t.Name).ToList();
        names.Should().OnlyHaveUniqueItems();
        names.Should().OnlyContain(n => System.Text.RegularExpressions.Regex.IsMatch(n, "^[a-z]+(_[a-z]+)*$"));
    }

    [Fact]
    public void AuditCodes_UniqueCamelCase()
    {
        var codes = McpToolCatalog.Production.Select(t => t.AuditCode).Append(McpToolCatalog.UnknownToolAuditCode).ToList();
        codes.Should().OnlyHaveUniqueItems();
        codes.Should().OnlyContain(c => System.Text.RegularExpressions.Regex.IsMatch(c, @"^[a-z][A-Za-z]*(\.[a-z][A-Za-z]*)+$"));
        codes.Should().OnlyContain(c => c.StartsWith("mcp."));
    }

    [Fact]
    public void Scopes_WhoamiNull_OthersBusinessRead_AllRead()
    {
        McpToolCatalog.Find("whoami")!.Scope.Should().BeNull();
        McpToolCatalog.Production.Where(t => t.Name != "whoami").Should().OnlyContain(t => t.Scope == McpScopes.BusinessRead);
        McpToolCatalog.Production.Should().OnlyContain(t => t.Risk == McpRisk.Read && !t.HighRiskFloor);
    }

    [Fact]
    public void Find_UnknownIsNull_CaseSensitive()
    {
        McpToolCatalog.Find("nope").Should().BeNull();
        McpToolCatalog.Find("WHOAMI").Should().BeNull();
        McpToolCatalog.Find("query_records")!.AuditCode.Should().Be("mcp.queryRecords");
    }
}
