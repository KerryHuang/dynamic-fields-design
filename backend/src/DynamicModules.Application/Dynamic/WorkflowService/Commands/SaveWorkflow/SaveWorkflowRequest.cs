using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;

public record SaveWorkflowRequest(string ModuleKey, WorkflowInput Workflow)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("workflow.save", AuditTargets.Module, ModuleKey);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
