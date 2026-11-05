namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>歷程的操作；存字串。</summary>
public enum RecordHistoryOperation { Create, Update, Delete }

/// <summary>記錄的欄位層級歷程（只新增、不修改，不繼承 AuditableEntity）。Create、Delete 列的 FieldId 為 null。</summary>
public class RecordHistory
{
    public long Id { get; set; }
    public long RecordId { get; set; }
    /// <summary>新記錄還沒有 Id 時掛這個導覽屬性，由 EF 在同一次 SaveChanges 補上 RecordId。</summary>
    public Record? Record { get; set; }
    public long? FieldId { get; set; }
    public RecordHistoryOperation Operation { get; set; }
    /// <summary>原始 JSON 文字；null 表示沒有值。</summary>
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string ChangedUserId { get; set; } = "";
    public DateTime ChangedTime { get; set; }
}
