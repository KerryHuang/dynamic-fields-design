namespace DynamicModules.Domain.Entities.Dynamic;

public class Record : AuditableEntity
{
    public long Id { get; set; }
    public long ModuleId { get; set; }
    public string Title { get; set; } = "";
    /// <summary>擴充模組記錄所屬的既有資料主鍵；standalone 為 null。</summary>
    public string? HostKey { get; set; }
    public string OwnerUserId { get; set; } = "";
    public string ValuesJson { get; set; } = "{}";
    /// <summary>GUID 字串，每次寫入都換新值。</summary>
    public string RowVersion { get; set; } = "";
    public bool DeleteFlag { get; set; }
    public List<RecordIndex> Index { get; set; } = [];
}
