using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Queries.AdminListGroups;

public class AdminListGroupsHandler(IPermissionRepository permissions) : IRequestHandler<AdminListGroupsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AdminListGroupsRequest request, CancellationToken ct)
    {
        var members = (await permissions.ListMembershipsAsync(ct)).GroupBy(m => m.GroupId)
            .ToDictionary(g => g.Key, g => g.Select(m => m.UserId).Order().ToList());
        return ServiceResult.Success((await permissions.ListGroupsAsync(ct))
            .Select(g => new AdminGroupResponse(g.Id, g.Key, g.NameI18n, members.GetValueOrDefault(g.Id, []))).ToList());
    }
}
