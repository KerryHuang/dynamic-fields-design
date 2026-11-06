using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Modules;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Queries.GetRecordByHost;

public class GetRecordByHostHandler(IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard,
    ReferenceResolver resolver)
    : IRequestHandler<GetRecordByHostRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetRecordByHostRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (!module.IsReadable() || ModuleRelations.IsDetail(module))
            return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        var access = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        if (access.Permissions is not { } p) return access.Denied!;
        var record = await records.GetByHostAsync(module.Id, request.HostKey, ct);
        // 範圍外與不存在同一個回應，不洩漏記錄存在
        if (record is null || (p.OwnerUserIds is not null && !p.OwnerUserIds.Contains(record.OwnerUserId)))
            return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var refs = await resolver.ResolveAsync(module, p, [record], ct);
        return ServiceResult.Success(record.ToResponse(module.Key, p, module, refs.GetValueOrDefault(record.Id)));
    }
}
