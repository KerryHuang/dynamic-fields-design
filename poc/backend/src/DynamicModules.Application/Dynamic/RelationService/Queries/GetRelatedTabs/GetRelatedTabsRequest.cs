using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.RelationService.Queries.GetRelatedTabs;

public record GetRelatedTabsRequest(long RecordId) : IRequest<ServiceResult>;
