using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;

public record SaveModulePermissionsRequest(string Key, ModulePermissionsDto Body)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: Key)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
