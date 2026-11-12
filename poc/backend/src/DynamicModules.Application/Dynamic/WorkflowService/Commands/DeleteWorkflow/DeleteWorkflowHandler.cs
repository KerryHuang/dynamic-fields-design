using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.WorkflowService.Commands.DeleteWorkflow;

public class DeleteWorkflowHandler(IModuleRepository modules, IUnitOfWork uow, ICurrentUser user, IBusinessTime time, PendingApprovalGuard pending)
    : IRequestHandler<DeleteWorkflowRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteWorkflowRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module?.Workflow is not { } workflow) return ServiceResult.NotFound("這個模組沒有狀態機");
        if (await pending.RejectWorkflowChangeAsync(module, null, ct) is { } pendingApproval) return pendingApproval;
        modules.RemoveWorkflow(workflow);
        module.Workflow = null;
        module.TouchVersion(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}
