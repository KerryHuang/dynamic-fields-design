using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.DeactivateModule;

public record DeactivateModuleRequest(string Key) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: Key)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
