namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>匯入的範本；Json 是收到的原文。</summary>
public class ModuleTemplate : AuditableEntity
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Json { get; set; } = "";
}
