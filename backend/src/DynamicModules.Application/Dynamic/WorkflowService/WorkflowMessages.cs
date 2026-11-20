using DynamicModules.Application.Common;

namespace DynamicModules.Application.Dynamic.WorkflowService;

/// <summary>流程設計存檔的訊息；上限是政策 A16（design.maxWorkflowStates／maxWorkflowTransitions）的生效值。</summary>
public static class WorkflowMessages
{
    public static string States(int max) => $"狀態必須是狀態欄位啟用中的選項，不重複，1 到 {max} 個";

    /// <summary>轉換的兩端、重複、群組錯誤；條數超過上限改用 <see cref="TooManyTransitions"/>。</summary>
    public const string Transitions = "轉移的兩端必須是不同的狀態、不重複，群組必須存在";

    /// <summary>轉換條數超過上限（四語，使用者 2026-11-20 裁決另給帶上限的訊息）。</summary>
    public static MessageRef TooManyTransitions(int max) => MessageRef.Of(MessageText.WorkflowTooManyTransitions, max);
}
