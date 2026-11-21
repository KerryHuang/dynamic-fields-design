namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>權杖撤銷原因（存成 camelCase 文字）。</summary>
public enum McpRevokeReason { Owner, Admin, UserDeactivated, PasswordReset }

/// <summary>
/// 個人存取權杖一列（MCP 設計 §13.3 <c>Dynamic_McpToken</c>）：只存明文的 SHA-256 雜湊與末四碼。不設外鍵：使用者被刪後，
/// 稽核以 <c>TokenId</c> 關聯的列仍要看得懂（同 <see cref="AuditLogEntry"/>）。
/// </summary>
public class McpToken
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>SHA-256 小寫十六進位，唯一。</summary>
    public string TokenHash { get; set; } = "";
    public string Last4 { get; set; } = "";
    /// <summary>範圍代碼以逗號串接（<c>McpScopes.Join</c>），讀回由應用層解析。</summary>
    public string Scopes { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    /// <summary>到期日當天營運時區結束的瞬間（UTC）；<c>ExpiresUtc &lt;= now</c> 即失效。</summary>
    public DateTime ExpiresUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public string? LastUsedIp { get; set; }
    public DateTime? RevokedUtc { get; set; }
    public string? RevokedBy { get; set; }
    public McpRevokeReason? RevokeReason { get; set; }

    // 技術限制（計畫判斷 2）
    public const int NameMax = 100, ScopesMax = 200;
}
