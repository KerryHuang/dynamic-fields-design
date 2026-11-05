using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.PermissionService.Queries.GetModulePermissions;

public class GetModulePermissionsHandler(IModuleRepository modules, IPermissionRepository permissions)
    : IRequestHandler<GetModulePermissionsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetModulePermissionsRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.Key, includeChildren: false, ct);
        if (module is null) return ServiceResult.NotFound("找不到模組");

        var rows = await permissions.LoadModuleRowsAsync(module, ct);
        return ServiceResult.Success(rows.ToResponse(await permissions.ListGroupsAsync(ct)));
    }
}
