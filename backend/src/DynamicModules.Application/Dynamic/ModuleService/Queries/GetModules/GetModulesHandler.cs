using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ModuleService.Queries.GetModules;

public class GetModulesHandler(IModuleRepository modules) : IRequestHandler<GetModulesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetModulesRequest request, CancellationToken ct)
    {
        var parents = await modules.ListDetailParentKeysAsync(ct);
        return ServiceResult.Success((await modules.ListAsync(ct))
            .Select(m => m.ToResponse(parents.GetValueOrDefault(m.Key))).ToList());
    }
}
