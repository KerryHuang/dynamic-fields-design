namespace DynamicModules.Application.Common.Security;

/// <summary>
/// 會改到模組定義的設計器請求，宣告它動到哪個模組（key、欄位、區塊或選項 Id 擇一）。
/// 那個模組是共用主檔的成員模組時，MasterDesignerBehavior 另外要求啟用中的系統管理員（共用主檔設計 §6 最後一點）。
/// </summary>
public interface IMasterDesignRequest
{
    MasterDesignTarget MasterTarget { get; }
}

public readonly record struct MasterDesignTarget(string? ModuleKey = null, long? FieldId = null, long? BlockId = null, long? OptionId = null);
