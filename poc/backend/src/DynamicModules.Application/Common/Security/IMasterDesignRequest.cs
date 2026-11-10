namespace DynamicModules.Application.Common.Security;

/// <summary>
/// 會改到模組定義或模組行為的設計器請求，宣告它動到哪些模組（每個目標以 key、欄位、區塊、選項 Id，
/// 或事件、檢查、轉換的代碼指定；代碼指定時以既有定義的模組為準）。
/// 任一個是共用主檔的成員模組時，MasterDesignerBehavior 另外要求啟用中的系統管理員（共用主檔設計 §6 最後一點）。
/// </summary>
public interface IMasterDesignRequest
{
    IReadOnlyList<MasterDesignTarget> MasterTargets { get; }
}

/// <param name="EventKey">既有連動事件的代碼：看它的來源模組。</param>
/// <param name="CheckKey">既有跨記錄檢查的代碼：看它的模組。</param>
/// <param name="ConversionKey">既有轉換的代碼：來源與目標模組都看。</param>
/// <param name="ApprovalFlowKey">既有簽核流程的代碼：看它的模組。</param>
public readonly record struct MasterDesignTarget(
    string? ModuleKey = null, long? FieldId = null, long? BlockId = null, long? OptionId = null,
    string? EventKey = null, string? CheckKey = null, string? ConversionKey = null, string? ApprovalFlowKey = null);
