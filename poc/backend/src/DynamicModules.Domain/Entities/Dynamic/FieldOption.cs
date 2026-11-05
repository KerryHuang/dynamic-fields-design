using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

public class FieldOption : AuditableEntity
{
    public long Id { get; set; }
    public long FieldId { get; set; }
    public I18nText LabelI18n { get; set; } = new();
    public string? Color { get; set; }
    public int Sequence { get; set; }
    public bool Active { get; set; }
}
