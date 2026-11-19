using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

public class Block : AuditableEntity
{
    public long Id { get; set; }
    public long ModuleId { get; set; }
    public I18nText LabelI18n { get; set; } = new();
    public int Sequence { get; set; }
    public bool Collapsed { get; set; }
    /// <summary>
    /// 表單每列欄數；寬度不足時前端會減少。新區塊的值由建立的入口依政策（form.defaultColumns、form.maxColumns）明確給，
    /// 沒有屬性初值；技術上限見 <c>PolicyCatalog.FormMaxColumns</c>。
    /// </summary>
    public int Columns { get; set; }
    public List<Field> Fields { get; set; } = [];
}
