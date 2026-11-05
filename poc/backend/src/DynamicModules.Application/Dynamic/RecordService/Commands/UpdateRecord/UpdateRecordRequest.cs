using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;

/// <param name="Details">鍵是明細模組的 Module.Key；只送新增、修改、刪除的列，沒列出的既有列不動。</param>
public record UpdateRecordRequest(
    long Id, Dictionary<string, JsonElement?> Values, string RowVersion, Dictionary<string, List<DetailRowInput>>? Details = null)
    : IRequest<ServiceResult>, ITransactionalRequest;

/// <summary>明細列：沒有 Id 是新增；有 Id 而 Delete 為 false 是修改（Values 只帶改動的欄位）；有 Id 而 Delete 為 true 是軟刪除。</summary>
public record DetailRowInput(
    long? Id, Dictionary<string, JsonElement?>? Values, string? RowVersion, [property: JsonPropertyName("_delete")] bool Delete = false);
