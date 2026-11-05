namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>記錄的型別化查詢索引列（衍生資料，不繼承 AuditableEntity）；主鍵 (RecordId, FieldId, Seq)。</summary>
public class RecordIndex
{
    public long RecordId { get; set; }
    public long FieldId { get; set; }
    public int Seq { get; set; }
    public string? TextValue { get; set; }
    public double? NumberValue { get; set; }
    public DateTime? DateValue { get; set; }
    public string? RefKey { get; set; }
}
