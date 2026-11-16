namespace DynamicModules.Domain.Entities.Dynamic;

public class ModuleDataScope
{
    public long ModuleId { get; set; }
    public long GroupId { get; set; }
    public DataScope Scope { get; set; }
}
