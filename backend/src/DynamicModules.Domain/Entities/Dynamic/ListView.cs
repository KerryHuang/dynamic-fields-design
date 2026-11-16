using System.Text.Json;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>清單檢視；OwnerUserId 為 null 是共用檢視，否則是該使用者的個人檢視。</summary>
public class ListView : AuditableEntity
{
    public long Id { get; set; }
    public long ModuleId { get; set; }
    public string? OwnerUserId { get; set; }
    public I18nText NameI18n { get; set; } = new();
    public List<long> Columns { get; set; } = [];
    public JsonElement? Filter { get; set; }
    public List<ViewSort> Sort { get; set; } = [];
    public bool IsDefault { get; set; }
}
