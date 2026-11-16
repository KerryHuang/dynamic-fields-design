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
    ICurrentUser user, IBusinessTime time,
    Dynamic.LedgerService.ImmutableDataGuard immutable, Dynamic.ApprovalService.PendingApprovalGuard pending,
    Dynamic.ApprovalService.LockedDataGuard lockedData)
    : IRequestHandler<DeleteFieldRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteFieldRequest request, CancellationToken ct)
    {
        var plan = await planner.PlanAsync(DeleteTarget.Field, null, request.FieldId, ct);
        if (plan is null) return ServiceResult.NotFound(DeleteMessages.NotFound(DeleteTarget.Field));
        if (!plan.Check.CanDelete) return ServiceResult.ConflictWith(DeleteMessages.Blocked, plan.Check);

        var module = (await modules.GetByIdAsync(plan.ModuleId, includeChildren: true, ct))!;
        // 清除會從記錄、索引與歷程拿掉這些欄位的值；Closed 的資料唯讀。沒有任何值要清的刪除（空欄位、空區塊）與 Inactive 模組一樣放行
        // 公式與彙總沒有歷程與索引列（Purge 全 0）但記錄上有值，所以守衛也要看這些欄位的值
        var fields = module.Blocks.SelectMany(b => b.Fields).Where(f => plan.FieldIds.Contains(f.Id)).ToList();
        if (await ClosedDataGuard.RejectFieldPurgeAsync(module, plan.Check.Purge, fields, deletion, ct) is { } closed) return closed;
        if (await immutable.RejectFieldPurgeAsync(module, plan.Check.Purge, fields, ct) is { } ledgerFrozen) return ledgerFrozen;
        if (await pending.RejectFieldPurgeAsync(module, plan.Check.Purge, fields, ct) is { } pendingApproval) return pendingApproval;
        // 鎖定資料守衛本身以「鎖定記錄上有值」判斷；公式與彙總沒有歷程與索引列（Purge 全 0），不能靠 Purge 閘門
        if (await lockedData.RejectPurgeAsync(module, fields, ct) is { } lockedValues) return lockedValues;
        if (plan.Check.Purge.Records != request.Purged) return ServiceResult.ConflictWith(DeleteMessages.PurgedMismatch, plan.Check);

        await FieldPurge.RemoveAsync(module, plan.FieldIds, [], deletion, uow, user, time, ct);
        return ServiceResult.Success();
    }
}
