using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>組織骨架的部門。Code 全站唯一；ParentId 為 null 是樹根；ManagerUserId 為 null 是主管空缺。設計 §11.2。</summary>
public class Department : AuditableEntity
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public I18nText NameI18n { get; set; } = new();
    public long? ParentId { get; set; }
    public string? ManagerUserId { get; set; }
    public int SortOrder { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>使用者所屬部門：一人一個部門（UserId 為主鍵）。</summary>
public class UserDepartment
{
    public string UserId { get; set; } = "";
    public long DepartmentId { get; set; }
}
