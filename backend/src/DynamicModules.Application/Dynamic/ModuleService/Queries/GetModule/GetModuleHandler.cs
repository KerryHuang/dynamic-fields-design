using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;

public class GetModuleHandler(IModuleRepository modules) : IRequestHandler<GetModuleRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetModuleRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.Key, includeChildren: true, ct);
        return module is null ? ServiceResult.NotFound(MessageRef.Of(MessageText.ModuleNotFound)) : ServiceResult.Success(module.ToDetail());
    }
}
