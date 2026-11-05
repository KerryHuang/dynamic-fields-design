namespace DynamicModules.Domain.Entities.Dynamic;

public abstract class AuditableEntity
{
    public string CreatedUserId { get; set; } = "";
    public DateTime CreatedTime { get; set; }
    public string? ModifiedUserId { get; set; }
    public DateTime? ModifiedTime { get; set; }

    public void StampCreated(string userId, DateTime now)
    {
        CreatedUserId = userId;
        CreatedTime = now;
    }

    public void StampModified(string userId, DateTime now)
    {
        ModifiedUserId = userId;
        ModifiedTime = now;
    }
}
