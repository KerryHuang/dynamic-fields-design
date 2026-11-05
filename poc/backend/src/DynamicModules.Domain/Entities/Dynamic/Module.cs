using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

public class Module : AuditableEntity
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string ProgramCode { get; set; } = "";
    public I18nText NameI18n { get; set; } = new();
    public string? Icon { get; set; }
    public string? Section { get; set; }
    public long? TitleFieldId { get; set; }
    public ModuleStatus Status { get; set; }
    public ModuleKind Kind { get; set; } = ModuleKind.Standalone;
    /// <summary>擴充模組所擴充的既有實體鍵；standalone 為 null。</summary>
    public string? HostEntityKey { get; set; }
    public int Version { get; set; }
    public List<Block> Blocks { get; set; } = [];
    public List<Rule> Rules { get; set; } = [];

    /// <summary>metadata 異動時呼叫：版本加一並更新修改稽核。</summary>
    public void TouchVersion(string userId, DateTime now)
    {
        Version++;
        StampModified(userId, now);
    }
}
