using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Queries.GetRecordByHost;

public record GetRecordByHostRequest(string ModuleKey, string HostKey) : IRequest<ServiceResult>;
