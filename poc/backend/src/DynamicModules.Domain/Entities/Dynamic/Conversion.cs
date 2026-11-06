using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>
/// 單據轉換定義：把來源模組的一筆記錄轉成目標模組的一筆記錄。
/// 追溯用目標單上的兩個唯讀欄位記來源（表頭：參照欄位；明細：整數欄位存來源明細列 Id），不另存連結表。
/// </summary>
public class Conversion : AuditableEntity
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public I18nText NameI18n { get; set; } = new();
    public long SourceModuleId { get; set; }
    public long TargetModuleId { get; set; }
    public bool Active { get; set; } = true;
    public ConversionDefinition Definition { get; set; } = new();
}

/// <summary>SourceStates 空代表不限來源狀態；Lines 為 null 是表頭轉換（同一張來源最多轉一次）。</summary>
public record ConversionDefinition(List<long> SourceStates, ConversionHeader Header, ConversionLines? Lines)
{
    public ConversionDefinition() : this([], new ConversionHeader(0, []), null) { }
}

/// <summary>SourceFieldId：目標表頭上指向來源表頭的唯讀參照欄位；Copy：來源表頭欄位 → 目標表頭欄位。</summary>
public record ConversionHeader(long SourceFieldId, List<FillMapping> Copy);

/// <summary>
/// 明細轉換。SourceModuleKey／TargetModuleKey 是來源與目標的明細模組；SourceLineFieldId 是目標明細上存來源明細列 Id 的唯讀整數欄位；
/// Quantity：來源明細的數量欄位 → 目標明細的數量欄位，剩餘量就是用這個欄位算。
/// </summary>
public record ConversionLines(
    string SourceModuleKey, string TargetModuleKey, long SourceLineFieldId, FillMapping Quantity, List<FillMapping> Copy);
