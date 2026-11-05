using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

public class Block : AuditableEntity
{
    public long Id { get; set; }
    public long ModuleId { get; set; }
    public I18nText LabelI18n { get; set; } = new();
    public int Sequence { get; set; }
    public bool Collapsed { get; set; }
    public List<Field> Fields { get; set; } = [];
}
