namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>POC 的使用者清單。</summary>
public class AppUser
{
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsSystemAdmin { get; set; }
    public string? PasswordHash { get; set; }
    public bool MustChangePassword { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDemoAccount { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>個人外觀模式（light／dark／system）；null 代表跟隨全站預設。</summary>
    public string? ThemeMode { get; set; }
    /// <summary>個人外觀風格（workbench／airy／enterprise／industrial／glass／bento／command／botanical）；null 代表跟隨全站預設。</summary>
    public string? ThemeStyle { get; set; }
}
