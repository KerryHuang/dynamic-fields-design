namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>模組對某個政策的覆寫值（設計 docs/superpowers/specs/2026-11-11-policy-settings-design.md §5）。值是標準字串，由政策登記解析。</summary>
public class ModulePolicy
{
    public long ModuleId { get; set; }
    public string Code { get; set; } = "";
    public string Value { get; set; } = "";
    public string ModifiedUserId { get; set; } = "";
    public DateTime ModifiedTime { get; set; }
}
