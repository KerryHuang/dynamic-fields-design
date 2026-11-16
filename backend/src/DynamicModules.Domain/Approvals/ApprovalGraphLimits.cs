namespace DynamicModules.Domain.Approvals;

/// <summary>流程圖的技術上限（流程圖設計 §15 可寫死清單）：保護系統與畫面，不是業務政策；業務上限走 PolicyCatalog。</summary>
public static class ApprovalGraphLimits
{
    public const int MaxNodes = 100, MaxEdges = 200, MaxNesting = 5;
    public const int MaxNotifyRecipients = 20, MaxResolvedRecipients = 200;
    public const int MaxTemplateTitleLength = 200, MaxTemplateBodyLength = 1000;
    /// <summary>設計器的版面與草稿大小、座標範圍（J66、J65）：版面最多 64 KB、座標絕對值 ≤ 100000、草稿最多 256 KB。</summary>
    public const int MaxLayoutBytes = 65536, MaxCoordinate = 100000, MaxDraftBytes = 262144;
    /// <summary>實例處理權的條件更新最多試幾次、第一次退避幾毫秒（每次加倍：20、40、80、160）；用完回 409 ApprovalBusy（§8.13、§15）。</summary>
    public const int MaxClaimAttempts = 5, ClaimBackoffMs = 20;
}
