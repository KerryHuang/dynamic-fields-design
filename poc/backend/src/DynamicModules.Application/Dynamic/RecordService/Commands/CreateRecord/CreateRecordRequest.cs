using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;

public record CreateRecordRequest(
    string ModuleKey, Dictionary<string, JsonElement?> Values, string? HostKey = null,
    Dictionary<string, List<DetailRowInput>>? Details = null)
    : IRequest<ServiceResult>, ITransactionalRequest;
