using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;

public record SaveModulePermissionsRequest(string Key, ModulePermissionsDto Body)
    : IRequest<ServiceResult>, ITransactionalRequest, IPermissionAdminRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("permission.update", AuditTargets.Module, Key);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: Key)];
}
