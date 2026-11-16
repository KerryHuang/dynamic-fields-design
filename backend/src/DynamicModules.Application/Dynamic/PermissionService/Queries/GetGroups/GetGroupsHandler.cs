using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.PermissionService.Queries.GetGroups;

public class GetGroupsHandler(IPermissionRepository permissions) : IRequestHandler<GetGroupsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetGroupsRequest request, CancellationToken ct)
    {
        var members = (await permissions.ListMembershipsAsync(ct)).ToLookup(m => m.GroupId, m => m.UserId);
        var groups = (await permissions.ListGroupsAsync(ct))
            .Select(g => new GroupResponse(g.Id, g.Key, g.NameI18n, members[g.Id].Order(StringComparer.Ordinal).ToList()))
            .ToList();
        return ServiceResult.Success(groups);
    }
}
