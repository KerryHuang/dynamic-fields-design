namespace DynamicModules.Domain.Mcp;

/// <summary>工具級別（設計 §6.1）：唯讀、寫入、高風險（要確認碼）。</summary>
public enum McpRisk { Read, Write, HighRisk }

/// <summary>
/// 一個 MCP 工具的宣告。<paramref name="Name"/> 是 snake_case 的協定名稱；<paramref name="AuditCode"/> 是 camelCase 的稽核動作
/// （<c>mcp.queryRecords</c>）；<paramref name="Scope"/> 是 null 代表不需範圍（<c>whoami</c>）。<paramref name="HighRiskFloor"/> 給 mcp2b 用。
/// <paramref name="LoopTargetArg"/> 是寫入迴圈偵測的目標記錄參數名（mcp2a 判斷 8）；null 代表不偵測。
/// </summary>
public sealed record McpToolDescriptor(string Name, string AuditCode, string? Scope, McpRisk Risk, bool HighRiskFloor = false,
    string? LoopTargetArg = null);

/// <summary>正式的工具清單（設計 §7.1、§7.2）：平台功能清單，寫在程式、不是政策（§5.2、§15）。mcp1a 12 個唯讀、mcp2a 7 個一般寫入。</summary>
public static class McpToolCatalog
{
    /// <summary>呼叫不存在的工具時的稽核動作。</summary>
    public const string UnknownToolAuditCode = "mcp.unknownTool";

    private static McpToolDescriptor Read(string name, string auditCode, string? scope = McpScopes.BusinessRead) =>
        new(name, auditCode, scope, McpRisk.Read);

    private static McpToolDescriptor Write(string name, string auditCode, string? loopTargetArg = null) =>
        new(name, auditCode, McpScopes.BusinessWrite, McpRisk.Write, LoopTargetArg: loopTargetArg);

    public static IReadOnlyList<McpToolDescriptor> Production { get; } =
    [
        Read("whoami", "mcp.whoami", scope: null),
        Read("list_modules", "mcp.listModules"),
        Read("describe_module", "mcp.describeModule"),
        Read("query_records", "mcp.queryRecords"),
        Read("get_record", "mcp.getRecord"),
        Read("get_record_history", "mcp.getRecordHistory"),
        Read("list_reports", "mcp.listReports"),
        Read("run_report", "mcp.runReport"),
        Read("list_my_tasks", "mcp.listMyTasks"),
        Read("get_task", "mcp.getTask"),
        Read("list_approval_tasks", "mcp.listApprovalTasks"),
        Read("get_record_approval", "mcp.getRecordApproval"),
        Write("create_record", "mcp.createRecord"),
        Write("update_record", "mcp.updateRecord", loopTargetArg: "recordId"),
        Write("create_task", "mcp.createTask"),
        Write("update_task", "mcp.updateTask"),
        Write("add_task_comment", "mcp.addTaskComment"),
        Write("submit_approval", "mcp.submitApproval", loopTargetArg: "recordId"),
        Write("withdraw_approval", "mcp.withdrawApproval"),
    ];

    /// <summary>依協定名稱找工具（區分大小寫）；找不到回 null。</summary>
    public static McpToolDescriptor? Find(string name) => Production.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
}
