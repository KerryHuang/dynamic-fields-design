using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

public class PermissionGroup
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public I18nText NameI18n { get; set; } = new();
}
