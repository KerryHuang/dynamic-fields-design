using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>
/// PermissionResolver 的輸入，一次讀齊。OptionScopes 含該模組所有選項的範圍列（不限群組）。
/// ActionScopes 是使用者所屬群組在這個模組的逐動作範圍（只有共用主檔模組有）；FieldGrants 是使用者所屬群組對限定可見欄位的可見列。
/// </summary>
public sealed record PermissionInputs(
    AppUser User, IReadOnlyList<long> GroupIds,
    IReadOnlyList<GroupProgramPrivilege> GroupPrivileges, UserProgramPrivilege? UserPrivilege,
    IReadOnlyList<ModuleDataScope> Scopes, IReadOnlyList<FieldPermission> FieldPermissions,
    IReadOnlyList<FieldOptionScope> OptionScopes, IReadOnlySet<string> GroupMateUserIds,
    IReadOnlyList<ModuleActionScope>? ActionScopes = null, IReadOnlyList<FieldGrant>? FieldGrants = null);
