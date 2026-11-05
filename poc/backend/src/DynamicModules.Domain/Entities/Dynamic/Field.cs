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
    public List<FieldOption> Options { get; set; } = [];
}
