namespace DynamicModules.Application.Dynamic.WorkflowService;

/// <summary>流程設計存檔的訊息；上限是政策 A16（design.maxWorkflowStates／maxWorkflowTransitions）的生效值。</summary>
public static class WorkflowMessages
{
    public static string States(int max) => $"狀態必須是狀態欄位啟用中的選項，不重複，1 到 {max} 個";

    /// <summary>轉換數超過上限也用這句（政策化之前就是如此，文字不帶上限）。</summary>
    public const string Transitions = "轉移的兩端必須是不同的狀態、不重複，群組必須存在";
}
