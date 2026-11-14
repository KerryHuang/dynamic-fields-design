namespace DynamicModules.Domain.Approvals;

/// <summary>流程圖的技術上限（流程圖設計 §15 可寫死清單）：保護系統與畫面，不是業務政策；業務上限走 PolicyCatalog。</summary>
public static class ApprovalGraphLimits
{
    public const int MaxNodes = 100, MaxEdges = 200, MaxNesting = 5;
    public const int MaxNotifyRecipients = 20, MaxResolvedRecipients = 200;
    public const int MaxTemplateTitleLength = 200, MaxTemplateBodyLength = 1000;
}
