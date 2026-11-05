using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.References;
using MediatR;

namespace DynamicModules.Application.Dynamic.EntityService.Queries.ListEntities;

public class ListEntitiesHandler(ReferenceProviderRegistry registry) : IRequestHandler<ListEntitiesRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(ListEntitiesRequest request, CancellationToken ct) =>
        Task.FromResult(ServiceResult.Success(registry.All.Select(p => new { key = p.EntityKey, name = p.Name }).ToList()));
}
