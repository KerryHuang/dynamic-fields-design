using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;

public record GetRecordRequest(long Id) : IRequest<ServiceResult>;
