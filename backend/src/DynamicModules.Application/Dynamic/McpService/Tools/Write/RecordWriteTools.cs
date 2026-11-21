using System.Globalization;
using DynamicModules.Application.Dynamic.McpService.Writes;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.Tools.Write;

/// <summary>
/// <c>create_record</c>（mcp2a 計畫判斷 2、7、16）：送畫面同一條 <see cref="CreateRecordRequest"/>。請求從白名單參數逐欄組出，
/// 系統填值的欄位鍵一律不給（只供內部程式用）；明細列只組新增列。
/// </summary>
public sealed class CreateRecordTool(ISender sender)
    : WriteTool(sender, "create_record", McpTexts.CreateRecordSchema, McpTexts.CreateRecordDescription)
{
    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var a = McpArgs.Of(call.Arguments);
        var moduleKey = a.String("moduleKey", required: true, maxLength: Domain.Entities.Dynamic.Module.KeyMaxLength);
        var values = a.Values("values");
        var details = a.Details("details", allowId: false);
        var hostKey = a.String("hostKey");
        if (a.HasErrors) return a.Invalid();

        var ignored = await IgnoredFieldsAsync(moduleKey!, values.Keys, ct);
        var r = await Sender.Send(new CreateRecordRequest(moduleKey!, values, hostKey, RecordWriteRows.Of(details)), ct);
        return r.IsSuccess
            ? Done(r, ignored, McpWriteTargets.Record, McpWriteTargets.IdOf(r.Data))
            : Done(r, ignored, "module", moduleKey);
    }
}

/// <summary>
/// <c>update_record</c>（mcp2a 計畫判斷 2、7、16、D3）：先送唯讀的 <see cref="GetRecordRequest"/> 取記錄所屬模組（算 <c>ignoredFields</c>），
/// 再送畫面同一條 <see cref="UpdateRecordRequest"/>。前置讀取失敗不擋寫入（handler 判斷權限；重送時讓冪等照常回放）。
/// 明細列不收刪除（D3），請求的刪除旗標固定 false。
/// </summary>
public sealed class UpdateRecordTool(ISender sender)
    : WriteTool(sender, "update_record", McpTexts.UpdateRecordSchema, McpTexts.UpdateRecordDescription)
{
    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var a = McpArgs.Of(call.Arguments);
        var id = a.Long("recordId", required: true);
        var rowVersion = a.String("rowVersion", required: true);
        var values = a.Values("values");
        var details = a.Details("details", allowId: true);
        if (a.HasErrors) return a.Invalid();

        var current = await Sender.Send(new GetRecordRequest(id!.Value), ct);
        var ignored = current is { IsSuccess: true, Data: DynamicRecordResponse record }
            ? await IgnoredFieldsAsync(record.ModuleKey, values.Keys, ct)
            : null;
        var r = await Sender.Send(new UpdateRecordRequest(id.Value, values, rowVersion!, RecordWriteRows.Of(details)), ct);
        return Done(r, ignored, McpWriteTargets.Record, id.Value.ToString(CultureInfo.InvariantCulture));
    }
}

internal static class RecordWriteRows
{
    /// <summary>明細列參數 → 寫入請求的明細列：只組 id、values、rowVersion，刪除旗標固定 false（D3）。</summary>
    public static Dictionary<string, List<DetailRowInput>>? Of(Dictionary<string, List<McpArgs.DetailRow>>? details) =>
        details?.ToDictionary(kv => kv.Key, kv => kv.Value.Select(row => new DetailRowInput(row.Id, row.Values, row.RowVersion)).ToList(),
            StringComparer.Ordinal);
}
