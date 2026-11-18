namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>稽核的行為者種類（維運設計 §17.4）；維運服務帳號（mcpO1）先預留。</summary>
public enum AuditActorKind { User, OpsService, System }

/// <summary>稽核的來源管道：畫面、MCP、排程工作、維運中心（O3）。</summary>
public enum AuditChannel { Ui, Mcp, Job, OpsCenter }

/// <summary>風險級別，照 MCP 的三級（維運設計 §17.2.1）。</summary>
public enum AuditRiskLevel { Read, Write, HighRisk }

/// <summary>稽核結果；畫面操作只記成功（O2-U9），其餘給登入失敗與 MCP。</summary>
public enum AuditResult { Success, Denied, Invalid, Failed }

/// <summary>高風險動作的確認結果（mcp1 填）。</summary>
public enum AuditConfirmationOutcome { Preview, Confirmed, Expired, Mismatch }

/// <summary>
/// 稽核紀錄一列（維運設計 §5.6、§17.4 <c>Dynamic_AuditLog</c>；MCP 設計 §8.1 共用）。只能新增：repository 沒有修改與單筆刪除，
/// 保留天數清理是唯一的刪除路徑（§17.9）。不設外鍵，使用者、權杖被刪後稽核仍要留。
/// </summary>
public class AuditLogEntry
{
    public long Id { get; set; }
    public DateTime OccurredUtc { get; set; }
    public string? ActorUserId { get; set; }
    public AuditActorKind ActorKind { get; set; }
    public AuditChannel Channel { get; set; }
    /// <summary>MCP 權杖 id（不存權杖本身）。</summary>
    public long? TokenId { get; set; }
    public string Action { get; set; } = "";
    public AuditRiskLevel RiskLevel { get; set; }
    public string? TargetKind { get; set; }
    public string? TargetId { get; set; }
    /// <summary>當下的顯示名快照（目標被刪後仍看得懂）。</summary>
    public string? TargetLabel { get; set; }
    /// <summary>MCP 參數的 SHA-256 小寫十六進位。</summary>
    public string? ArgumentsHash { get; set; }
    public int? ResultCount { get; set; }
    /// <summary>摘要與差異（<c>AuditSummary</c> 組出、已遮蔽）；上限 <see cref="SummaryMax"/>。</summary>
    public string? SummaryJson { get; set; }
    public long? ConfirmationId { get; set; }
    public AuditConfirmationOutcome? ConfirmationOutcome { get; set; }
    public AuditResult Result { get; set; }
    public string? ReasonCode { get; set; }
    public string? RequestId { get; set; }
    public string? ClientIp { get; set; }

    // 技術限制（維運設計 §17.9）
    public const int ActionMax = 100, TargetKindMax = 30, TargetIdMax = 100, TargetLabelMax = 200, ReasonCodeMax = 50, SummaryMax = 16_000;
}
