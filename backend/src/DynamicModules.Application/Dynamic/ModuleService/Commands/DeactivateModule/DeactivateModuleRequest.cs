using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.DeactivateModule;

public record DeactivateModuleRequest(string Key) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("module.deactivate", AuditTargets.Module, Key);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: Key)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
