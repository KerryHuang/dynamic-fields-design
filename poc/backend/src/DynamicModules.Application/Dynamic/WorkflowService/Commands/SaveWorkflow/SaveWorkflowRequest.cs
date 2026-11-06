using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;

public record SaveWorkflowRequest(string ModuleKey, WorkflowInput Workflow)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
