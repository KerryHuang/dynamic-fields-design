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
    IModuleRepository modules, IDynamicAccessGuard guard, ModuleSchemaBuilder builder, WorkflowEnforcer workflow) : IRequestHandler<GetModuleSchemaRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetModuleSchemaRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is not { Status: ModuleStatus.Active } || ModuleRelations.IsDetail(module)) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);

        var access = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        if (access.Permissions is not { } p) return access.Denied!;

        return ServiceResult.Success(await builder.BuildAsync(module, p, async child =>
            (await guard.AuthorizeDetailAsync(module, child, PermissionAction.Read, ct)).Permissions, ct,
            await workflow.PermittedTransitionsAsync(module, p, ct)));
    }
}
