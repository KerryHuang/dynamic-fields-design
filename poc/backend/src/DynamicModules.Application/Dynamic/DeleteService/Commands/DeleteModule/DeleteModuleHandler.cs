using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteModule;

public class DeleteModuleHandler(
    DeletePlanner planner, IModuleRepository modules, IRecordRepository records, IDeletionRepository deletion, IUnitOfWork uow)
    : IRequestHandler<DeleteModuleRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteModuleRequest request, CancellationToken ct)
    {
        var plan = await planner.PlanAsync(DeleteTarget.Module, request.Key, 0, ct);
        if (plan is null) return ServiceResult.NotFound(DeleteMessages.NotFound(DeleteTarget.Module));
        if (!plan.Check.CanDelete) return ServiceResult.ConflictWith(DeleteMessages.Blocked, plan.Check);
        if (plan.Check.Purge.Records != request.Purged) return ServiceResult.ConflictWith(DeleteMessages.PurgedMismatch, plan.Check);

        // 交易由 TransactionBehavior 包住：清除與刪除定義一起提交或回滾
        var module = (await modules.GetByIdAsync(plan.ModuleId, includeChildren: true, ct))!;
        await deletion.PurgeModuleAsync(module, ct);
        modules.Remove(module);
        await uow.CommitAsync(ct);
        // 事後條件：不可留下任何記錄列（讀已提交的資料庫上，檢查與清除之間可能有人寫入）
        if (await records.AnyByModuleAsync(plan.ModuleId, ct))
            throw new InvalidOperationException("刪除模組後仍有記錄，已回滾");
        return ServiceResult.Success();
    }
}
