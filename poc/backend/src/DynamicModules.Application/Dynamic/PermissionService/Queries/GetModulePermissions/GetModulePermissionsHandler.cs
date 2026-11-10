using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.PermissionService.Queries.GetModulePermissions;

public class GetModulePermissionsHandler(
    IModuleRepository modules, IPermissionRepository permissions, AppService.SharedMasters.MasterGrants masterGrants, ISharedMasterRepository shared)
    : IRequestHandler<GetModulePermissionsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetModulePermissionsRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.Key, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.ModuleNotFound));

        var rows = await permissions.LoadModuleRowsAsync(module, ct);
        var groups = await permissions.ListGroupsAsync(ct);
        var visibility = await masterGrants.VisibilityAsync(module, ct);
        // 共用主檔模組逐動作回傳範圍（設計 §6）；限定可見欄位回傳可見群組與來源
        return ServiceResult.Success(await masterGrants.IsMasterModuleAsync(module.Id, ct)
            ? rows.ToResponse(groups, await shared.ListActionScopesAsync(module.Id, ct), visibility)
            : rows.ToResponse(groups, fieldGrants: visibility.Count > 0 ? visibility : null));
    }
}
