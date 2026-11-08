using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;

/// <param name="SystemKeys">值由系統填入（事件效果）的欄位鍵，不受唯讀限制；只供內部使用，HTTP 的請求本文是另一個型別。</param>
/// <param name="Details">鍵是明細模組的 Module.Key；只送新增、修改、刪除的列，沒列出的既有列不動。</param>
public record UpdateRecordRequest(
    long Id, Dictionary<string, JsonElement?> Values, string RowVersion, Dictionary<string, List<DetailRowInput>>? Details = null,
    IReadOnlySet<string>? SystemKeys = null)
    : IRequest<ServiceResult>, IRecordWriteRequest;

/// <summary>明細列：沒有 Id 是新增；有 Id 而 Delete 為 false 是修改（Values 只帶改動的欄位）；有 Id 而 Delete 為 true 是軟刪除。</summary>
/// <param name="SystemKeys">值由系統填入（單據轉換）的欄位鍵，不受唯讀限制。只供內部使用：JsonIgnore，用戶端送的不會被採用。</param>
public record DetailRowInput(
    long? Id, Dictionary<string, JsonElement?>? Values, string? RowVersion, [property: JsonPropertyName("_delete")] bool Delete = false,
    [property: JsonIgnore] IReadOnlySet<string>? SystemKeys = null);
