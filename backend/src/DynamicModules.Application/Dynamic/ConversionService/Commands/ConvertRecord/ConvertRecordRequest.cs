using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.ConversionService.Commands.ConvertRecord;

/// <param name="RowVersion">來源記錄使用者看到的版本；與目前不符回 409。</param>
/// <param name="Values">目標表頭欄位的覆寫值（例如改交期），照一般新增的規則處理；追溯欄位一律由系統填。</param>
/// <param name="Lines">要轉的來源明細列與數量；明細轉換至少一列，表頭轉換不用。</param>
public record ConvertRecordRequest(
    long SourceId, string ConversionKey, string? RowVersion, Dictionary<string, JsonElement?>? Values, List<ConvertLineInput>? Lines)
    : IRequest<ServiceResult>, IRecordWriteRequest;
