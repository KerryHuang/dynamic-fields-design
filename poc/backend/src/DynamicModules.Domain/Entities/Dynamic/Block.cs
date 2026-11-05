using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

public class Block : AuditableEntity
{
    public long Id { get; set; }
    public long ModuleId { get; set; }
    public I18nText LabelI18n { get; set; } = new();
    public int Sequence { get; set; }
    public bool Collapsed { get; set; }
    /// <summary>表單每列欄數（1 到 3）；寬度不足時前端會減少。</summary>
    public int Columns { get; set; } = 2;
    public List<Field> Fields { get; set; } = [];
}
