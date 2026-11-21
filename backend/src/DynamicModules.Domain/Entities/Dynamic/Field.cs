using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

public class Field : AuditableEntity
{
    public long Id { get; set; }
    public long ModuleId { get; set; }
    public long BlockId { get; set; }
    public string Key { get; set; } = "";
    public I18nText LabelI18n { get; set; } = new();
    public FieldType Type { get; set; }
    public FieldConfig Config { get; set; } = new();
    public int Sequence { get; set; }
    public FieldStatus Status { get; set; }
    /// <summary>貢獻欄位：只有 Dynamic_FieldGrant 列出的群組與系統管理員看得到。建立時決定，貢獻者刪除後也不清掉。</summary>
    public bool VisibilityRestricted { get; set; }
    /// <summary>「不提供給 AI」：MCP 通道的權限解析把它當隱藏欄位（權限軸設計 §7.3、§7.4 守衛欄）。出廠 false。</summary>
    public bool ExcludeFromAi { get; set; }
    public List<FieldOption> Options { get; set; } = [];
}
