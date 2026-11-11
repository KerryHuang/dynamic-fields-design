using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

public class Block : AuditableEntity
{
    public long Id { get; set; }
    public long ModuleId { get; set; }
    public I18nText LabelI18n { get; set; } = new();
    public int Sequence { get; set; }
    public bool Collapsed { get; set; }
    /// <summary>新區塊的每列欄數。待併入政策設定（p2）：改成系統管理員可設定。</summary>
    public const int DefaultColumns = 4;
    /// <summary>每列欄數可設的上限（下限 1）；前端寬度不足時會自動減少欄數。</summary>
    public const int MaxColumns = 4;

    /// <summary>表單每列欄數（1 到 <see cref="MaxColumns"/>）；寬度不足時前端會減少。</summary>
    public int Columns { get; set; } = DefaultColumns;
    public List<Field> Fields { get; set; } = [];
}
