using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;

// SystemKeys：值由系統填入（單據轉換）的欄位鍵，不受唯讀限制；只供內部程式使用，HTTP 的請求本文是另一個型別，不會帶進來。

public record CreateRecordRequest(
    string ModuleKey, Dictionary<string, JsonElement?> Values, string? HostKey = null,
    Dictionary<string, List<DetailRowInput>>? Details = null, IReadOnlySet<string>? SystemKeys = null)
    : IRequest<ServiceResult>, ITransactionalRequest;
