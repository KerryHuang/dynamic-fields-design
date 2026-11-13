using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.WorkflowService.Queries.GetWorkflow;

public class GetWorkflowHandler(IModuleRepository modules, WorkflowApprovals approvals) : IRequestHandler<GetWorkflowRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetWorkflowRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.ModuleNotFound));
        if (module.Workflow is not { } w) return ServiceResult.NotFound("這個模組沒有狀態機");
        return ServiceResult.Success(w.ToResponse(await approvals.LockedFieldIdsAsync(module, w, ct),
            await approvals.EditableFieldIdsAsync(module, w, ct)));
    }
}
