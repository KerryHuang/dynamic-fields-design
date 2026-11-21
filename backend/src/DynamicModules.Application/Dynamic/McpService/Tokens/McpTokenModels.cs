namespace DynamicModules.Application.Dynamic.McpService.Tokens;

/// <summary>
/// 權杖清單的一列（mcp1a 計畫判斷 7）：不含雜湊與明文。<c>Status</c> 是 <c>active</c>／<c>expired</c>／<c>revoked</c>，
/// <c>RevokeReason</c> 是 <c>owner</c>／<c>admin</c>／<c>userDeactivated</c>／<c>passwordReset</c>；<c>ExpiresOn</c> 是營運時區的到期日（當天結束時失效）。
/// </summary>
public sealed record McpTokenItem(
    long Id, string UserId, string Name, string Last4, IReadOnlyList<string> Scopes, DateTime CreatedUtc, DateTime ExpiresUtc, DateOnly ExpiresOn,
    DateTime? LastUsedUtc, string? LastUsedIp, string Status, DateTime? RevokedUtc, string? RevokedBy, string? RevokeReason, int TodayCalls);

/// <summary>建立成功的回應：<c>Token</c> 是明文，只在這一次回傳。</summary>
public sealed record McpTokenCreated(string Token, McpTokenItem Item);

/// <summary>Claude Code 與 Codex 的設定片段；權杖用環境變數 <c>FORMWRIGHT_TOKEN</c>，不放明文。</summary>
public sealed record McpClientExamples(string ClaudeCode, string Codex);

/// <summary>
/// 本人的 AI 連線資訊：<c>Reason</c> 是 <c>disabled</c>／<c>notAllowed</c>／<c>policyInvalid</c>／<c>demoSite</c>／<c>accountBlocked</c>，能用時是 null；
/// 兩個日期是營運時區的日期。<c>Scopes</c> 是可建立的範圍，<c>DefaultScopes</c> 是建立對話框預設勾選的範圍。
/// </summary>
public sealed record McpInfoResponse(
    bool Enabled, bool CanUse, string? Reason, IReadOnlyList<string> Scopes, IReadOnlyList<string> DefaultScopes, DateOnly DefaultExpiresOn, DateOnly MaxExpiresOn, string ServerUrl,
    McpClientExamples Examples);

/// <summary>一把權杖最近的工具呼叫：<c>Tool</c> 是協定上的工具名稱（稽核的 TargetLabel），<c>Result</c> 是 camelCase 的稽核結果。</summary>
public sealed record McpTokenCall(long Id, string Action, string? Tool, DateTime OccurredUtc, string Result, string? ReasonCode);
