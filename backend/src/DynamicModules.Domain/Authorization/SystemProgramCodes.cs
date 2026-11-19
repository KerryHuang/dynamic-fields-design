namespace DynamicModules.Domain.Authorization;

/// <summary>
/// 系統權限代碼（權限軸設計 §6.1，T3）：不是業務模組，存在 <c>Dynamic_Program</c> 與兩張授權表，和業務模組共用表但不看 <c>Active</c>。
/// 屬可寫死清單（§16）：安全與權限檢查，不是業務政策。
/// </summary>
public static class SystemProgramCodes
{
    /// <summary>設計器：建模組、欄位、規則、流程、報表、檢視。</summary>
    public const string Designer = "DYN00000";

    /// <summary>權限設定：決定誰能看、能做什麼；只用 <c>CanEdit</c> 一個位元（T2）。</summary>
    public const string PermissionAdmin = "SYS00001";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Designer, PermissionAdmin };
}
