using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.EntityService.Queries.GetEntityLight;

public record GetEntityLightRequest(string EntityKey, string? Keyword, int Page = 1, int Size = 20) : IRequest<ServiceResult>;
