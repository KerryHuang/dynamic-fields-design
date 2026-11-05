using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.SchemaService.Queries.GetPreviewSchema;

public class GetPreviewSchemaHandler(IModuleRepository modules, ModuleSchemaBuilder builder) : IRequestHandler<GetPreviewSchemaRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetPreviewSchemaRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);

        // 預覽一律以「全部動作、看得到全部欄位」的管理員權限組；明細模組同樣，且只列啟用中的明細
        var admin = PermissionResolver.ForSystemAdmin();
        return ServiceResult.Success(await builder.BuildAsync(module, admin, _ => Task.FromResult<EffectivePermissions?>(admin), ct));
    }
}
