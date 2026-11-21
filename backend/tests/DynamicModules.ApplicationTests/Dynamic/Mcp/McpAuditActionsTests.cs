using DynamicModules.Application.Common.Audit;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>mcp1a 判斷 17：稽核目錄的 mcp 分組 17 個代碼；工具代碼與正式工具清單一致。</summary>
public class McpAuditActionsTests
{
    private static readonly string[] Tools =
    [
        "mcp.whoami", "mcp.listModules", "mcp.describeModule", "mcp.queryRecords", "mcp.getRecord", "mcp.getRecordHistory",
        "mcp.listReports", "mcp.runReport", "mcp.listMyTasks", "mcp.getTask", "mcp.listApprovalTasks", "mcp.getRecordApproval",
    ];

    [Fact]
    public void McpGroup_HasSeventeenCodes()
    {
        AuditActions.CodesOf(AuditActions.Mcp).Should().BeEquivalentTo(
            Tools.Concat(["mcp.access", "mcp.token.create", "mcp.token.revoke", "mcp.token.revokeAll", "mcp.unknownTool"]));
    }

    [Fact]
    public void ToolCodes_AreRead_OthersWrite()
    {
        foreach (var code in Tools.Append(McpToolCatalog.UnknownToolAuditCode))
            AuditActions.Find(code)!.Risk.Should().Be(AuditRiskLevel.Read, code);
        foreach (var code in new[] { "mcp.access", "mcp.token.create", "mcp.token.revoke", "mcp.token.revokeAll" })
            AuditActions.Find(code)!.Risk.Should().Be(AuditRiskLevel.Write, code);
    }

    [Fact]
    public void EveryCatalogTool_HasRegisteredAuditCode() =>
        McpToolCatalog.Production.Should().OnlyContain(t => AuditActions.Find(t.AuditCode) != null && AuditActions.Find(t.AuditCode)!.Group == AuditActions.Mcp);
}
