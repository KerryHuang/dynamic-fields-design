using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>某模組的全部權限設定列（權限設定頁一次讀寫的單位）。</summary>
public record ModulePermissionRows(
    List<GroupProgramPrivilege> GroupPrivileges,
    List<UserProgramPrivilege> UserPrivileges,
    List<ModuleDataScope> Scopes,
    List<FieldPermission> FieldPermissions,
    List<FieldOptionScope> OptionScopes);
