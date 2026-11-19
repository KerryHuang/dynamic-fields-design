using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Queries.AdminListUsers;

public class AdminListUsersHandler(IPermissionRepository permissions, IOrganizationRepository org, TimeProvider time, Common.ICurrentLocale locale)
    : IRequestHandler<AdminListUsersRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AdminListUsersRequest request, CancellationToken ct)
    {
        var groups = (await permissions.ListMembershipsAsync(ct)).GroupBy(m => m.UserId)
            .ToDictionary(g => g.Key, g => g.Select(m => m.GroupId).Order().ToList());
        var departments = await org.ListUserDepartmentsAsync(ct);
        var system = await permissions.LoadSystemPrivilegeRowsAsync(ct);
        var groupList = await permissions.ListGroupsAsync(ct);
        var now = time.GetUtcNow();
        return ServiceResult.Success((await permissions.ListUsersAsync(ct)).Select(u =>
        {
            var mine = groups.GetValueOrDefault(u.UserId, []);
            var personal = system.Users.Where(r => r.UserId == u.UserId).ToList();
            var fromGroups = system.Groups.Where(r => mine.Contains(r.GroupId)).ToList();
            // 來源：有任一系統權限的群組（個人那份不重複列），系統管理員另一筆
            var sources = groupList.Where(g => mine.Contains(g.Id))
                .Select(g => (Group: g, Direct: Common.Security.SystemPrivilegeRules.Direct(fromGroups.Where(r => r.GroupId == g.Id))))
                .Where(x => x.Direct.Any)
                .Select(x => new SystemPrivilegeSourceResponse("group", x.Group.Id, x.Group.NameI18n.Resolve(locale.Locale), x.Direct.Designer, x.Direct.PermissionAdmin))
                .ToList();
            if (u.IsSystemAdmin) sources.Add(new SystemPrivilegeSourceResponse("systemAdmin"));
            return new AdminUserResponse(
                u.UserId, u.DisplayName, u.IsActive, u.IsSystemAdmin, u.IsDemoAccount,
                u.LockedUntil > now, mine,
                departments.TryGetValue(u.UserId, out var d) ? d : null,
                Common.Security.SystemPrivilegeRules.Direct(personal),
                Common.Security.SystemPrivilegeRules.Effective(u, fromGroups.Cast<ProgramPrivilege>().Concat(personal)),
                sources);
        }).ToList());
    }
}
