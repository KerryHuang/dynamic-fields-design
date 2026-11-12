using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Modules;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;

public class GetRecordHandler(IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard,
    ReferenceResolver resolver, DetailReader details, Dynamic.EventService.EventFactLock factLock, ApprovalService.ApprovalReader approvals,
    ApprovalService.ApprovalReadGrant grant, ApprovalService.ApprovalLockService locks)
    : IRequestHandler<GetRecordRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetRecordRequest request, CancellationToken ct)
    {
        var record = await records.GetAsync(request.Id, ct);
        if (record is null) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (!module.IsReadable()) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        if (ModuleRelations.IsDetail(module)) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var access = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        var p = access.Permissions;
        var outOfScope = p?.OwnerUserIds is { } owners && !owners.Contains(record.OwnerUserId);
        var granted = false;
        if (p is null || outOfScope)
        {
            // 簽核人的臨時讀取權（簽核設計 §10.1）：只對這一筆；沒有時維持原回應（沒有 Read 403、範圍外 404，主控裁決 1）
            if (await grant.ForRecordAsync(module, record, ct) is not { } g)
                return p is null ? access.Denied! : ServiceResult.NotFound(RecordMapper.RecordNotFound);   // 範圍外與不存在同一個回應
            (p, granted) = (g, true);
        }
        var refs = await resolver.ResolveAsync(module, p, [record], ct);
        return ServiceResult.Success(record.ToResponse(module.Key, p, module, refs.GetValueOrDefault(record.Id))
            with
            {
                Details = await details.ReadAsync(module, record, PermissionAction.Read, ct,
                    granted ? child => grant.ForDetailAsync(module, child, ct) : null), FrozenByEffects = await factLock.IsFrozenAsync(record.Id, ct),
                Approval = await approvals.SummaryAsync(module, record, ct), ApprovalGrant = granted,
                ApprovalLock = await LockOfAsync(module, record, p, ct)
            });
    }

    private async Task<ApprovalService.RecordApprovalLock?> LockOfAsync(Module module, Record record, EffectivePermissions p, CancellationToken ct)
    {
        // 先用純函式判斷狀態，非鎖定狀態（最常被讀）不用付算鎖定欄位的查詢成本
        if (module.Workflow is not { } wf || !ApprovalTopology.IsLocked(wf, wf.StateOf(RecordValueProcessor.Parse(record.ValuesJson)))) return null;
        var locked = await locks.ForModuleAsync(module, null, ct);
        var keys = module.Blocks.OrderBy(b => b.Sequence).ThenBy(b => b.Id)
            .SelectMany(b => b.Fields.Where(f => f.Status == FieldStatus.Active).OrderBy(f => f.Sequence).ThenBy(f => f.Id))
            .Where(f => locked.FieldIds.Contains(f.Id) && p.CanSee(f)).Select(f => f.Key).ToList();
        return new(keys, locked.DetailRowsLocked);
    }
}
