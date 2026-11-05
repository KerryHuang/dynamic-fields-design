using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.RuleService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.RuleService.Queries.GetRules;

public class GetRulesHandler(IModuleRepository modules) : IRequestHandler<GetRulesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetRulesRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        return module is null ? ServiceResult.NotFound("找不到模組") : ServiceResult.Success(module.Rules.ToResponse());
    }
}
