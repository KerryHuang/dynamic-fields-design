using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>PermissionResolver 的輸入，一次讀齊。OptionScopes 含該模組所有選項的範圍列（不限群組）。</summary>
public sealed record PermissionInputs(
    AppUser User, IReadOnlyList<long> GroupIds,
    IReadOnlyList<GroupProgramPrivilege> GroupPrivileges, UserProgramPrivilege? UserPrivilege,
    IReadOnlyList<ModuleDataScope> Scopes, IReadOnlyList<FieldPermission> FieldPermissions,
    IReadOnlyList<FieldOptionScope> OptionScopes, IReadOnlySet<string> GroupMateUserIds);
