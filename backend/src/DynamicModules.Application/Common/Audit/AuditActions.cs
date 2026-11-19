using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Common.Audit;

/// <summary>
/// 稽核動作目錄（維運設計 §17.2.1、B13）：每個動作代碼一列，含分組、風險級別、四語標籤鍵。代碼一律 <c>&lt;分組或對象&gt;.&lt;動詞&gt;</c>、camelCase。
/// mcp1 把 <c>mcp.&lt;工具&gt;</c> 加進同一份目錄。畫面操作標高風險的至少是 C7 地板那幾類：改權限、套用或刪除應用程式套件、刪模組或欄位，
/// 另加授予或拿掉系統管理員（使用者的建立與修改可能帶這個旗標，所以整個標高風險）。
/// </summary>
public static class AuditActions
{
    public sealed record Entry(string Code, string Group, AuditRiskLevel Risk)
    {
        /// <summary>前端四語標籤鍵（o2e 稽核頁）。</summary>
        public string LabelKey => $"audit.actions.{Code}";
    }

    public const string Auth = "auth", User = "user", Group = "group", Permission = "permission", Policy = "policy", Org = "org",
        Design = "design", App = "app", Ops = "ops", Mcp = "mcp";

    public static readonly IReadOnlyList<string> Groups = [Auth, User, Group, Permission, Policy, Org, Design, App, Ops, Mcp];

    private const AuditRiskLevel W = AuditRiskLevel.Write, H = AuditRiskLevel.HighRisk;

    public static IReadOnlyList<Entry> All { get; } =
    [
        // 登入（不經 AuditBehavior；§17.2.1）
        new("login.succeeded", Auth, W),
        new("login.failed", Auth, W),
        // 使用者與群組
        new("user.create", User, H),
        new("user.update", User, H),
        new("user.setActive", User, W),
        new("user.resetPassword", User, W),
        new("group.create", Group, W),
        new("group.rename", Group, W),
        new("group.delete", Group, W),
        new("group.setMembers", Group, W),
        // 權限
        new("permission.update", Permission, H),
        // 政策
        new("policy.set", Policy, W),
        new("policy.moduleSet", Policy, W),
        // 組織
        new("department.create", Org, W),
        new("department.update", Org, W),
        new("department.delete", Org, W),
        new("department.setUser", Org, W),
        // 設計：模組、欄位、區塊、選項
        new("module.create", Design, W),
        new("module.update", Design, W),
        new("module.activate", Design, W),
        new("module.deactivate", Design, W),
        new("module.delete", Design, H),
        new("block.add", Design, W),
        new("block.update", Design, W),
        new("block.delete", Design, W),
        new("field.add", Design, W),
        new("field.update", Design, W),
        new("field.reorder", Design, W),
        new("field.setStatus", Design, W),
        new("field.delete", Design, H),
        new("option.add", Design, W),
        new("option.update", Design, W),
        // 設計：規則、流程、事件、檢查、分錄、任務規則、簽核、列印、轉單、範本
        new("rule.save", Design, W),
        new("workflow.save", Design, W),
        new("workflow.delete", Design, W),
        new("event.create", Design, W),
        new("event.update", Design, W),
        new("event.delete", Design, W),
        new("check.create", Design, W),
        new("check.update", Design, W),
        new("check.delete", Design, W),
        new("ledger.save", Design, W),
        new("ledger.delete", Design, W),
        new("taskRule.create", Design, W),
        new("taskRule.update", Design, W),
        new("taskRule.delete", Design, W),
        // 規則自動暫停與恢復（維運設計 §17.2.7）：暫停由系統寫在補記失敗的交易裡，恢復經 AuditBehavior
        new("taskRule.autoPause", Design, W),
        new("taskRule.resume", Design, W),
        new("approvalFlow.create", Design, W),
        new("approvalFlow.update", Design, W),
        new("approvalFlow.delete", Design, W),
        new("approvalFlow.reorder", Design, W),
        new("approvalFlow.createDraft", Design, W),
        new("approvalFlow.saveDraft", Design, W),
        new("approvalFlow.discardDraft", Design, W),
        new("approvalFlow.publish", Design, W),
        new("printTemplate.create", Design, W),
        new("printTemplate.update", Design, W),
        new("printTemplate.delete", Design, W),
        new("conversion.create", Design, W),
        new("conversion.update", Design, W),
        new("conversion.delete", Design, W),
        new("template.import", Design, W),
        new("template.instantiate", Design, W),
        new("dashboard.create", Design, W),
        new("dashboard.update", Design, W),
        new("dashboard.delete", Design, W),
        new("dashboard.reorder", Design, W),
        new("dashboard.setAssignments", Design, W),
        new("theme.update", Design, W),
        // 應用程式套件
        new("app.open", App, H),
        new("app.import", App, H),
        new("app.delete", App, H),
        new("app.close", App, W),
        new("app.reopen", App, W),
        new("app.integrationEnable", App, W),
        new("app.integrationDisable", App, W),
        new("app.integrationRetry", App, W),
        // 維運
        new("ops.retention.purge", Ops, W),
        new("healthCheck.update", Ops, W),
        new("incident.acknowledge", Ops, W),
        new("incident.resolve", Ops, W),
        new("ops.channel.update", Ops, W),
        new("ops.channel.test", Ops, W),
        new("mailSettings.update", Ops, W),
        new("mailSettings.test", Ops, W),
    ];

    private static readonly Dictionary<string, Entry> ByCode = All.ToDictionary(e => e.Code, StringComparer.Ordinal);

    public static Entry? Find(string code) => ByCode.GetValueOrDefault(code);

    /// <summary>動作分組展開成代碼清單（查詢用 IN，不用 LIKE 前綴；O2-T18）。不存在的分組回空清單。</summary>
    public static IReadOnlyList<string> CodesOf(string group) => [.. All.Where(e => e.Group == group).Select(e => e.Code)];
}
