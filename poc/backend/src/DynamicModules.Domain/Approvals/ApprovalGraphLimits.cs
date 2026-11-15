namespace DynamicModules.Domain.Approvals;

/// <summary>流程圖的技術上限（流程圖設計 §15 可寫死清單）：保護系統與畫面，不是業務政策；業務上限走 PolicyCatalog。</summary>
public static class ApprovalGraphLimits
{
    public const int MaxNodes = 100, MaxEdges = 200, MaxNesting = 5;
    public const int MaxNotifyRecipients = 20, MaxResolvedRecipients = 200;
    public const int MaxTemplateTitleLength = 200, MaxTemplateBodyLength = 1000;
    /// <summary>實例處理權的條件更新最多試幾次、第一次退避幾毫秒（每次加倍：20、40、80、160）；用完回 409 ApprovalBusy（§8.13、§15）。</summary>
    public const int MaxClaimAttempts = 5, ClaimBackoffMs = 20;
}
