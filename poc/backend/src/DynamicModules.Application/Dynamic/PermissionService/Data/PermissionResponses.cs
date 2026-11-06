using DynamicModules.Application.Dynamic.ThemeService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.PermissionService.Data;

public record PrivilegesDto(bool Read, bool Add, bool Edit, bool Delete, bool PrintExport, bool Pricing, bool Approve)
{
    public bool IsEmpty => this == new PrivilegesDto(false, false, false, false, false, false, false);
}

public record GroupPermissionDto(long GroupId, PrivilegesDto Privileges, DataScope? Scope);

public record UserPermissionDto(string UserId, PrivilegesDto Privileges);

public record FieldPermissionDto(long FieldId, long GroupId, FieldAccess Access);

public record OptionScopeDto(long OptionId, List<long> GroupIds);

/// <summary>PUT 的 body。</summary>
public record ModulePermissionsDto(
    List<GroupPermissionDto> Groups, List<UserPermissionDto> Users,
    List<FieldPermissionDto> Fields, List<OptionScopeDto> Options);

/// <summary>GET 的結果（形狀同 PUT body）。</summary>
public record ModulePermissionsResponse(
    List<GroupPermissionDto> Groups, List<UserPermissionDto> Users,
    List<FieldPermissionDto> Fields, List<OptionScopeDto> Options);

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

    /// <summary>列出所有群組；沒有授權列的群組 privileges 全 false、scope 為 null。</summary>
    public static ModulePermissionsResponse ToResponse(this ModulePermissionRows rows, IEnumerable<PermissionGroup> groups) =>
        new(
            groups.OrderBy(g => g.Id).Select(g => new GroupPermissionDto(
                g.Id,
                rows.GroupPrivileges.FirstOrDefault(p => p.GroupId == g.Id)?.ToDto() ?? new PrivilegesDto(false, false, false, false, false, false, false),
                rows.Scopes.FirstOrDefault(s => s.GroupId == g.Id)?.Scope)).ToList(),
            rows.UserPrivileges.OrderBy(p => p.UserId, StringComparer.Ordinal)
                .Select(p => new UserPermissionDto(p.UserId, p.ToDto())).ToList(),
            rows.FieldPermissions.OrderBy(p => p.FieldId).ThenBy(p => p.GroupId)
                .Select(p => new FieldPermissionDto(p.FieldId, p.GroupId, p.Access)).ToList(),
            rows.OptionScopes.GroupBy(s => s.OptionId).OrderBy(g => g.Key)
                .Select(g => new OptionScopeDto(g.Key, g.Select(s => s.GroupId).Order().ToList())).ToList());
}
