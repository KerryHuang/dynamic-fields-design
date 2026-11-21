using DynamicModules.Domain.Mcp;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Mcp;

public class McpToolCatalogTests
{
    [Fact]
    public void Production_NineteenTools_IncludingWhoami() =>
        McpToolCatalog.Production.Select(t => t.Name).Should().BeEquivalentTo(
            "whoami", "list_modules", "describe_module", "query_records", "get_record", "get_record_history",
            "list_reports", "run_report", "list_my_tasks", "get_task", "list_approval_tasks", "get_record_approval",
            "create_record", "update_record", "create_task", "update_task", "add_task_comment", "submit_approval", "withdraw_approval");

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
    public void Scopes_ReadToolsBusinessRead_WriteToolsBusinessWrite()
    {
        McpToolCatalog.Find("whoami")!.Scope.Should().BeNull();
        var read = McpToolCatalog.Production.Where(t => t.Risk == McpRisk.Read).ToList();
        var write = McpToolCatalog.Production.Where(t => t.Risk != McpRisk.Read).ToList();
        (read.Count, write.Count).Should().Be((12, 7));
        read.Where(t => t.Name != "whoami").Should().OnlyContain(t => t.Scope == McpScopes.BusinessRead);
        write.Should().OnlyContain(t => t.Scope == McpScopes.BusinessWrite && t.Risk == McpRisk.Write);
        McpToolCatalog.Production.Should().OnlyContain(t => !t.HighRiskFloor);
        McpToolCatalog.Production.Where(t => t.LoopTargetArg != null).Select(t => (t.Name, t.LoopTargetArg))
            .Should().BeEquivalentTo([("update_record", "recordId"), ("submit_approval", "recordId")]);
    }

    [Fact]
    public void Find_UnknownIsNull_CaseSensitive()
    {
        McpToolCatalog.Find("nope").Should().BeNull();
        McpToolCatalog.Find("WHOAMI").Should().BeNull();
        McpToolCatalog.Find("query_records")!.AuditCode.Should().Be("mcp.queryRecords");
    }
}
