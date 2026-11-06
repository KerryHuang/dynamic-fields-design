using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.WorkflowService.Queries.GetWorkflow;

public class GetWorkflowHandler(IModuleRepository modules) : IRequestHandler<GetWorkflowRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetWorkflowRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound("找不到模組");
        return module.Workflow is { } w ? ServiceResult.Success(w.ToResponse()) : ServiceResult.NotFound("這個模組沒有狀態機");
    }
}
