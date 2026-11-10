using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Modules;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;

public class GetRecordHandler(IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard,
    ReferenceResolver resolver, DetailReader details, Dynamic.EventService.EventFactLock factLock, ApprovalService.ApprovalReader approvals)
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
        if (access.Permissions is not { } p) return access.Denied!;
        // 範圍外與不存在同一個回應，不洩漏記錄存在
        if (p.OwnerUserIds is not null && !p.OwnerUserIds.Contains(record.OwnerUserId))
            return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var refs = await resolver.ResolveAsync(module, p, [record], ct);
        return ServiceResult.Success(record.ToResponse(module.Key, p, module, refs.GetValueOrDefault(record.Id))
            with
            {
                Details = await details.ReadAsync(module, record, PermissionAction.Read, ct), FrozenByEffects = await factLock.IsFrozenAsync(record.Id, ct),
                Approval = await approvals.SummaryAsync(module, record, ct)
            });
    }
}
