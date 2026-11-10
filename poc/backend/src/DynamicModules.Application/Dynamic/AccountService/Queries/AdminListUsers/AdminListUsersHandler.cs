using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Queries.AdminListUsers;

public class AdminListUsersHandler(IPermissionRepository permissions, IOrganizationRepository org, TimeProvider time)
    : IRequestHandler<AdminListUsersRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AdminListUsersRequest request, CancellationToken ct)
    {
        var groups = (await permissions.ListMembershipsAsync(ct)).GroupBy(m => m.UserId)
            .ToDictionary(g => g.Key, g => g.Select(m => m.GroupId).Order().ToList());
        var departments = await org.ListUserDepartmentsAsync(ct);
        var now = time.GetUtcNow();
        return ServiceResult.Success((await permissions.ListUsersAsync(ct)).Select(u => new AdminUserResponse(
            u.UserId, u.DisplayName, u.IsActive, u.IsSystemAdmin, u.IsDemoAccount,
            u.LockedUntil > now, groups.GetValueOrDefault(u.UserId, []),
            departments.TryGetValue(u.UserId, out var d) ? d : null)).ToList());
    }
}
