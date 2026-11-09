using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.WorkflowService.Commands.DeleteWorkflow;

public record DeleteWorkflowRequest(string ModuleKey) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
