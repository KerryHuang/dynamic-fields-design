namespace DynamicModules.Application.Common.Audit;

/// <summary>
/// 管理與設計類請求的稽核宣告（維運設計 §17.2.1、O2-T2）：成功時由 <see cref="Behaviors.AuditBehavior{TReq,TRes}"/>
/// 在同一個交易寫一列稽核。新舊值由 handler 經 <see cref="AuditContext"/> 補。
/// </summary>
public interface IAuditedRequest
{
    AuditAction Audit { get; }
}

/// <summary>
/// 動作代碼（登記在 <see cref="AuditActions"/>）與目標；請求裡沒有的目標 id 或顯示名由 handler 以 <see cref="AuditContext.Target"/> 補。
/// 新增類的目標 id 是請求帶的代碼（模組 key、事件代碼…），由系統編號的（群組、儀表板）只記顯示名。
/// </summary>
public readonly record struct AuditAction(string Code, string? TargetKind = null, string? TargetId = null, string? TargetLabel = null)
{
    public static AuditAction For(string code, string? targetKind, object? targetId, string? targetLabel = null) =>
        new(code, targetKind, targetId switch
        {
            null => null,
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => targetId.ToString(),
        }, targetLabel);
}

/// <summary>稽核目標種類（§17.4 <c>TargetKind</c>）。</summary>
public static class AuditTargets
{
    public const string Module = "module", Field = "field", Block = "block", Option = "option", Group = "group", User = "user",
        Policy = "policy", Job = "job", App = "app", Rule = "rule", Event = "event", Check = "check", Ledger = "ledger",
        TaskRule = "taskRule", Workflow = "workflow", ApprovalFlow = "approvalFlow", PrintTemplate = "printTemplate",
        Conversion = "conversion", Dashboard = "dashboard", Department = "department", Template = "template", Theme = "theme",
        Integration = "integration", HealthCheck = "healthCheck", Incident = "incident", Channel = "channel",
        MailSettings = "mailSettings", Locale = "locale";
}
