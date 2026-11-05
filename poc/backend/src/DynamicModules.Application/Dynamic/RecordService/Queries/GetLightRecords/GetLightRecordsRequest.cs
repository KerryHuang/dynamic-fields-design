using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Queries.GetLightRecords;

public record GetLightRecordsRequest(string ModuleKey, string? Keyword, int Page, int Size) : IRequest<ServiceResult>;
