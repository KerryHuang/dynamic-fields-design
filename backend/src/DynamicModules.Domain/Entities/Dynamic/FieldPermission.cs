namespace DynamicModules.Domain.Entities.Dynamic;

public class FieldPermission
{
    public long FieldId { get; set; }
    public long GroupId { get; set; }
    public FieldAccess Access { get; set; }
}
