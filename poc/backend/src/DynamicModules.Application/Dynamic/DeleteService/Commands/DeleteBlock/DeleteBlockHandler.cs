using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteBlock;

public class DeleteBlockHandler(
    DeletePlanner planner, IModuleRepository modules, IDeletionRepository deletion, IUnitOfWork uow,
    ICurrentUser user, IBusinessTime time)
    : IRequestHandler<DeleteBlockRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteBlockRequest request, CancellationToken ct)
    {
        var plan = await planner.PlanAsync(DeleteTarget.Block, null, request.BlockId, ct);
        if (plan is null) return ServiceResult.NotFound(DeleteMessages.NotFound(DeleteTarget.Block));
        if (!plan.Check.CanDelete) return ServiceResult.ConflictWith(DeleteMessages.Blocked, plan.Check);

        var module = (await modules.GetByIdAsync(plan.ModuleId, includeChildren: true, ct))!;
        // 清除會從記錄、索引與歷程拿掉這些欄位的值；Closed 的資料唯讀。沒有任何值要清的刪除（空欄位、空區塊）與 Inactive 模組一樣放行
        if (ClosedDataGuard.RejectPurge(module, plan.Check.Purge) is { } closed) return closed;
        if (plan.Check.Purge.Records != request.Purged) return ServiceResult.ConflictWith(DeleteMessages.PurgedMismatch, plan.Check);

        await FieldPurge.RemoveAsync(module, plan.FieldIds, plan.BlockIds, deletion, uow, user, time, ct);
        return ServiceResult.Success();
    }
}
