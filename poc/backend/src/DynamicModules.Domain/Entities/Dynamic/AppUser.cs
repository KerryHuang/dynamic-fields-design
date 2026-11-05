namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>POC 的使用者清單。</summary>
public class AppUser
{
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsSystemAdmin { get; set; }
}
