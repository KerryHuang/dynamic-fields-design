using DynamicModules.Application.Dynamic.ThemeService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.PermissionService.Data;

public record PrivilegesDto(bool Read, bool Add, bool Edit, bool Delete, bool PrintExport, bool Pricing, bool Approve)
{
    public bool IsEmpty => this == new PrivilegesDto(false, false, false, false, false, false, false);
}

/// <param name="ActionScopes">共用主檔模組才有：每個動作各自的資料範圍（設計 §6）；一般模組是 null，看 Scope。</param>
public record GroupPermissionDto(long GroupId, PrivilegesDto Privileges, DataScope? Scope, List<ActionScopeDto>? ActionScopes = null);

public record ActionScopeDto(PermissionAction Action, DataScope Scope);

/// <summary>PUT：管理員手動授予某個限定可見欄位給哪些群組（只寫手動來源）。</summary>
public record FieldGrantDto(long FieldId, List<long> GroupIds);

/// <summary>
/// GET：限定可見欄位的可見群組與來源（app＝貢獻者給的，不能在這裡取消；manual＝管理員加的）。
/// GroupIds 是手動授予的群組，和 PUT 的 <see cref="FieldGrantDto"/> 同形，原樣送回就不會改動任何授予。
/// </summary>
public record FieldVisibilityDto(long FieldId, List<long> GroupIds, List<FieldVisibilityGroupDto> Groups);

public record FieldVisibilityGroupDto(long GroupId, GrantSource Source, string? AppKey);

public record UserPermissionDto(string UserId, PrivilegesDto Privileges);

public record FieldPermissionDto(long FieldId, long GroupId, FieldAccess Access);

public record OptionScopeDto(long OptionId, List<long> GroupIds);

/// <summary>PUT 的 body。</summary>
/// <param name="FieldGrants">null 表示不動手動可見授予（舊的呼叫端）；給了就是這個模組全部的手動授予。</param>
public record ModulePermissionsDto(
    List<GroupPermissionDto> Groups, List<UserPermissionDto> Users,
    List<FieldPermissionDto> Fields, List<OptionScopeDto> Options, List<FieldGrantDto>? FieldGrants = null);

/// <summary>GET 的結果（形狀同 PUT body）。</summary>
/// <param name="SharedMaster">共用主檔模組：群組權限逐動作設定範圍，並列出限定可見欄位的可見群組。</param>
public record ModulePermissionsResponse(
    List<GroupPermissionDto> Groups, List<UserPermissionDto> Users,
    List<FieldPermissionDto> Fields, List<OptionScopeDto> Options,
    bool SharedMaster = false, List<FieldVisibilityDto>? FieldGrants = null);

public record GroupResponse(long Id, string Key, I18nText Name, List<string> Members);

public record GroupBriefResponse(long Id, string Key, I18nText Name);

public record UserResponse(string UserId, string DisplayName);

public record DesignerActionsResponse(bool Read, bool Add, bool Edit, bool Delete);

public record MeResponse(
    string UserId, string DisplayName, bool IsSystemAdmin, bool MustChangePassword,
    List<GroupBriefResponse> Groups, DesignerActionsResponse Designer, bool IsDemoAccount, MeThemeResponse Theme);

public static class PermissionMapping
{
    public static PrivilegesDto ToDto(this ProgramPrivilege p) =>
        new(p.CanRead, p.CanAdd, p.CanEdit, p.CanDelete, p.CanPrint, p.CanAmt, p.CanApprove);

    /// <summary>
    /// 列出所有群組；沒有授權列的群組 privileges 全 false、scope 為 null。
    /// 共用主檔模組傳入 actionScopes（逐動作範圍）與 fieldGrants（限定可見欄位的可見列），一般模組都是 null。
    /// </summary>
    public static ModulePermissionsResponse ToResponse(this ModulePermissionRows rows, IEnumerable<PermissionGroup> groups,
        IReadOnlyList<ModuleActionScope>? actionScopes = null, IReadOnlyList<FieldVisibilityDto>? fieldGrants = null) =>
        new(
            groups.OrderBy(g => g.Id).Select(g => new GroupPermissionDto(
                g.Id,
                rows.GroupPrivileges.FirstOrDefault(p => p.GroupId == g.Id)?.ToDto() ?? new PrivilegesDto(false, false, false, false, false, false, false),
                rows.Scopes.FirstOrDefault(s => s.GroupId == g.Id)?.Scope,
                actionScopes?.Where(s => s.GroupId == g.Id).OrderBy(s => s.Action).Select(s => new ActionScopeDto(s.Action, s.Scope)).ToList())).ToList(),
            rows.UserPrivileges.OrderBy(p => p.UserId, StringComparer.Ordinal)
                .Select(p => new UserPermissionDto(p.UserId, p.ToDto())).ToList(),
            rows.FieldPermissions.OrderBy(p => p.FieldId).ThenBy(p => p.GroupId)
                .Select(p => new FieldPermissionDto(p.FieldId, p.GroupId, p.Access)).ToList(),
            rows.OptionScopes.GroupBy(s => s.OptionId).OrderBy(g => g.Key)
                .Select(g => new OptionScopeDto(g.Key, g.Select(s => s.GroupId).Order().ToList())).ToList(),
            actionScopes is not null, fieldGrants?.ToList());
}
