using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteField;

public class DeleteFieldHandler(
    DeletePlanner planner, IModuleRepository modules, IDeletionRepository deletion, IUnitOfWork uow,
    ICurrentUser user, IBusinessTime time)
    : IRequestHandler<DeleteFieldRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteFieldRequest request, CancellationToken ct)
    {
        var plan = await planner.PlanAsync(DeleteTarget.Field, null, request.FieldId, ct);
        if (plan is null) return ServiceResult.NotFound(DeleteMessages.NotFound(DeleteTarget.Field));
        if (!plan.Check.CanDelete) return ServiceResult.ConflictWith(DeleteMessages.Blocked, plan.Check);
        if (plan.Check.Purge.Records != request.Purged) return ServiceResult.ConflictWith(DeleteMessages.PurgedMismatch, plan.Check);

        var module = (await modules.GetByIdAsync(plan.ModuleId, includeChildren: true, ct))!;
        await FieldPurge.RemoveAsync(module, plan.FieldIds, [], deletion, uow, user, time, ct);
        return ServiceResult.Success();
    }
}
