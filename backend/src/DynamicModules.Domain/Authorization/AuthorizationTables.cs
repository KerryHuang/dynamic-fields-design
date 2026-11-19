namespace DynamicModules.Domain.Authorization;

/// <summary>
/// 授權寫入邊界的守衛表（權限軸設計 §7.4）：寫入要有對應的寫入許可，由 SaveChanges 攔截器與四個批次入口把關。
/// 不列 <c>Dynamic_PermissionGroup</c>（群組本身沒有權限內容）、報表分享、儀表板指派、任務檢視分享（共用檢視歸設計）。
/// </summary>
public static class AuthorizationTables
{
    public const string GroupProgramPrivilege = "Dynamic_GroupProgramPrivilege";
    public const string UserProgramPrivilege = "Dynamic_UserProgramPrivilege";
    public const string FieldPermission = "Dynamic_FieldPermission";
    public const string ModuleDataScope = "Dynamic_ModuleDataScope";
    public const string FieldOptionScope = "Dynamic_FieldOptionScope";
    public const string FieldGrant = "Dynamic_FieldGrant";
    public const string AppGrant = "Dynamic_AppGrant";
    public const string ModuleActionScope = "Dynamic_ModuleActionScope";
    public const string UserGroupMapping = "Dynamic_UserGroupMapping";

    /// <summary>有守衛欄的欄位定義表：<c>VisibilityRestricted</c> 改成 false（放寬可見性）要許可；整列刪除不在此限。</summary>
    public const string Field = "Dynamic_Field";

    /// <summary>不算守衛表、但名稱像授權表的（清單測試 9 的排除清單）。</summary>
    public const string PermissionGroup = "Dynamic_PermissionGroup";

    public static readonly IReadOnlySet<string> Guarded = new HashSet<string>(StringComparer.Ordinal)
    {
        GroupProgramPrivilege, UserProgramPrivilege, FieldPermission, ModuleDataScope, FieldOptionScope,
        FieldGrant, AppGrant, ModuleActionScope, UserGroupMapping,
    };
}
