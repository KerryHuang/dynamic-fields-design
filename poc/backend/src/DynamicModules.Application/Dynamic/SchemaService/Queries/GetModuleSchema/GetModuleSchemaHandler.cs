using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Application.Dynamic.WorkflowService;
using MediatR;

namespace DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;

public class GetModuleSchemaHandler(
    IModuleRepository modules, IDynamicAccessGuard guard, ModuleSchemaBuilder builder, WorkflowEnforcer workflow,
    IRecordRepository records, ApprovalService.ApprovalReadGrant grant) : IRequestHandler<GetModuleSchemaRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetModuleSchemaRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (!module.IsReadable() || ModuleRelations.IsDetail(module)) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);

        var access = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        if (access.Permissions is not { } p)
        {
            // 簽核人的臨時讀取（簽核設計 §10.1）：只在一般讀取被拒、而且指定了一筆記錄時
            if (request.RecordId is not { } recordId) return access.Denied!;
            var record = await records.GetAsync(recordId, ct);
            if (record is null || record.ModuleId != module.Id) return access.Denied!;   // 不存在、別的模組都和沒有權限一樣，不洩漏存在性
            if (await grant.ForRecordAsync(module, record, ct) is not { } g) return access.Denied!;
            return ServiceResult.Success(await builder.BuildAsync(module, g, child => grant.ForDetailAsync(module, child, ct), ct, []));
        }

        return ServiceResult.Success(await builder.BuildAsync(module, p, async child =>
            (await guard.AuthorizeDetailAsync(module, child, PermissionAction.Read, ct)).Permissions, ct,
            await workflow.PermittedTransitionsAsync(module, p, ct)));
    }
}
