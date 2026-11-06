using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ConversionService.Queries.GetConversions;

public record GetConversionsRequest(string? Key = null) : IRequest<ServiceResult>, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public class GetConversionsHandler(IConversionRepository conversions, IModuleRepository modules)
    : IRequestHandler<GetConversionsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetConversionsRequest request, CancellationToken ct)
    {
        var keyById = (await modules.ListAsync(ct)).ToDictionary(m => m.Id, m => m.Key);
        ConversionResponse Map(Domain.Entities.Dynamic.Conversion c) =>
            c.ToResponse(keyById.GetValueOrDefault(c.SourceModuleId, ""), keyById.GetValueOrDefault(c.TargetModuleId, ""));

        if (request.Key is null) return ServiceResult.Success((await conversions.ListAsync(ct)).Select(Map).ToList());
        var one = await conversions.GetByKeyAsync(request.Key, ct);
        return one is null ? ServiceResult.NotFound("找不到轉換定義") : ServiceResult.Success(Map(one));
    }
}
