using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.DeleteService.Queries.CheckDelete;

public class CheckDeleteHandler(DeletePlanner planner) : IRequestHandler<CheckDeleteRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CheckDeleteRequest request, CancellationToken ct)
    {
        var plan = await planner.PlanAsync(request.Target, request.Key, request.Id, ct);
        return plan is null ? ServiceResult.NotFound(DeleteMessages.NotFound(request.Target)) : ServiceResult.Success(plan.Check);
    }
}
