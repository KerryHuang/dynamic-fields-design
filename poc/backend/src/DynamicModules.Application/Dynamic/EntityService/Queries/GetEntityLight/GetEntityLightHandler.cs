using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.References;
using MediatR;

namespace DynamicModules.Application.Dynamic.EntityService.Queries.GetEntityLight;

public class GetEntityLightHandler(ReferenceProviderRegistry registry) : IRequestHandler<GetEntityLightRequest, ServiceResult>
{
    public const int MaxSize = 50;

    public async Task<ServiceResult> Handle(GetEntityLightRequest request, CancellationToken ct)
    {
        var provider = registry.Get(request.EntityKey);
        if (provider is null) return ServiceResult.NotFound("既有實體不存在");

        var page = Math.Max(request.Page, 1);
        var size = Math.Clamp(request.Size, 1, MaxSize);
        return ServiceResult.Success(await provider.LightAsync(request.Keyword, page, size, ct));
    }
}
