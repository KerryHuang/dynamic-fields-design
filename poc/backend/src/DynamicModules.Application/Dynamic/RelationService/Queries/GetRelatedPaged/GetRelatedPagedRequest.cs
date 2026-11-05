using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.RelationService.Queries.GetRelatedPaged;

public record GetRelatedPagedRequest(long RecordId, long FieldId, int Page = 1, int Size = 20) : IRequest<ServiceResult>;
