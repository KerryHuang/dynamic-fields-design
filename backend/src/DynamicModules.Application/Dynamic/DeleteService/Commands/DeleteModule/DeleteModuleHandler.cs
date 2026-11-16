using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteModule;

public class DeleteModuleHandler(DeletePlanner planner, ModuleRemover remover)
    : IRequestHandler<DeleteModuleRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteModuleRequest request, CancellationToken ct)
    {
        var plan = await planner.PlanAsync(DeleteTarget.Module, request.Key, 0, ct);
        if (plan is null) return ServiceResult.NotFound(DeleteMessages.NotFound(DeleteTarget.Module));
        if (!plan.Check.CanDelete) return ServiceResult.ConflictWith(DeleteMessages.Blocked, plan.Check);
        if (plan.Check.Purge.Records != request.Purged) return ServiceResult.ConflictWith(DeleteMessages.PurgedMismatch, plan.Check);

        // 交易由 TransactionBehavior 包住：清除與刪除定義一起提交或回滾
        await remover.RemoveAsync(plan.ModuleId, ct);
        return ServiceResult.Success();
    }
}
