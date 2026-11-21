using System.Text.Json;
using DynamicModules.Application.Common.Audit;
using DynamicModules.ApplicationTests.Common;
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

    private static readonly string[] WriteTools =
    [
        "mcp.createRecord", "mcp.updateRecord", "mcp.createTask", "mcp.updateTask", "mcp.addTaskComment", "mcp.submitApproval",
        "mcp.withdrawApproval",
    ];

    [Fact]
    public void McpGroup_HasTwentyFourCodes()
    {
        AuditActions.CodesOf(AuditActions.Mcp).Should().BeEquivalentTo(
            Tools.Concat(WriteTools).Concat(["mcp.access", "mcp.token.create", "mcp.token.revoke", "mcp.token.revokeAll", "mcp.unknownTool"]));
    }

    [Fact]
    public void ToolCodes_AreRead_OthersWrite()
    {
        foreach (var code in Tools.Append(McpToolCatalog.UnknownToolAuditCode))
            AuditActions.Find(code)!.Risk.Should().Be(AuditRiskLevel.Read, code);
        foreach (var code in WriteTools.Concat(["mcp.access", "mcp.token.create", "mcp.token.revoke", "mcp.token.revokeAll"]))
            AuditActions.Find(code)!.Risk.Should().Be(AuditRiskLevel.Write, code);
    }

    [Fact]
    public void EveryCatalogTool_HasRegisteredAuditCode() =>
        McpToolCatalog.Production.Should().OnlyContain(t => AuditActions.Find(t.AuditCode) != null && AuditActions.Find(t.AuditCode)!.Group == AuditActions.Mcp);

    [Theory]
    [InlineData("zh-TW")]
    [InlineData("en-US")]
    [InlineData("th")]
    [InlineData("vi")]
    public void WriteTools_LabelledInEveryBuiltinLocale(string locale)
    {
        var ui = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuiltinServerTextTests.RepoRoot(), "shared", "i18n", locale, "ui.json"))).RootElement;
        var mcp = ui.GetProperty("audit").GetProperty("actions").GetProperty("mcp");
        foreach (var code in WriteTools)
            mcp.GetProperty(code["mcp.".Length..]).GetString().Should().NotBeNullOrWhiteSpace(code);
    }
}
