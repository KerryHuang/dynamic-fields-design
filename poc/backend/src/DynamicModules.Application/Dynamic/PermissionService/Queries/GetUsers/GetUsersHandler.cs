using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.PermissionService.Queries.GetUsers;

public class GetUsersHandler(IPermissionRepository permissions) : IRequestHandler<GetUsersRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetUsersRequest request, CancellationToken ct) =>
        ServiceResult.Success((await permissions.ListUsersAsync(ct))
            .Select(u => new UserResponse(u.UserId, u.DisplayName)).ToList());
}
