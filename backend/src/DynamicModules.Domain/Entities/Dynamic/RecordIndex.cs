namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>記錄的型別化查詢索引列（衍生資料，不繼承 AuditableEntity）；主鍵 (RecordId, FieldId, Seq)。</summary>
public class RecordIndex
{
    public long RecordId { get; set; }
    public long FieldId { get; set; }
    public int Seq { get; set; }
    public string? TextValue { get; set; }
    public double? NumberValue { get; set; }
    /// <summary>數字型別的精確整數值：十進位值 × 10^Scale，寫索引時由 decimal 算出；跨記錄驗證的 SUM 用它（超出 long 範圍時為 null）。</summary>
    public long? ScaledValue { get; set; }
    public DateTime? DateValue { get; set; }
    public string? RefKey { get; set; }
    /// <summary>TextValue 是原值被截斷後的前段（超過 <c>HandlerHelpers.MaxIndexTextLength</c>）；報表的 SQL 聚合遇到截斷列就退回記憶體路徑。</summary>
    public bool TextTruncated { get; set; }
}
