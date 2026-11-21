using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.SchemaService.Data;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.Tools.Write;

/// <summary>
/// 一般寫入工具的共用基底（mcp2a 計畫判斷 2、5、7、16）。工具只送既有 request、從白名單參數逐欄組請求；權限與實際忽略哪些欄位
/// 全由既有 handler 決定。<c>idempotencyKey</c> 的驗證、上膛與稽核在執行器與 <c>McpWriteAuditBehavior</c>，工具不碰。
/// 成功結果的 <c>data</c> 是 handler 的 <c>Data</c> 原樣（冪等回放時是 <c>JsonElement</c>，不轉型）。
/// </summary>
public abstract class WriteTool(ISender sender, string name, string schema, string description) : IMcpTool
{
    protected ISender Sender => sender;
    public McpToolDescriptor Descriptor { get; } = McpToolCatalog.Find(name)!;
    public string InputSchemaJson => schema;
    public string Description => description;
    public abstract Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct);

    /// <summary>不能由寫入改值的欄位型別（計算欄位）。</summary>
    private static readonly HashSet<FieldType> Computed = [FieldType.Formula, FieldType.Rollup, FieldType.AutoNumber];

    /// <summary>
    /// <c>meta.ignoredFields</c>（判斷 7）：這次送了、但依 describe_module 的 schema 不可寫的表頭欄位 key，依 Ordinal 排序、不分原因
    /// （不存在、被遮、隱藏、唯讀、計算欄位一律同樣呈現，AI 無法藉此確認被遮欄位存在，D1）。schema 讀不到時回 null（meta 省略），
    /// 寫入照送。清單只是提示：值照原樣送給寫入請求，實際忽略由 <c>RecordValueProcessor</c> 決定。明細列不計入（已知限制）。
    /// </summary>
    protected async Task<IReadOnlyList<string>?> IgnoredFieldsAsync(string moduleKey, IEnumerable<string> keys, CancellationToken ct)
    {
        var r = await Sender.Send(new GetModuleSchemaRequest(moduleKey), ct);
        if (!r.IsSuccess || r.Data is not ModuleSchemaResponse schema) return null;
        var writable = schema.Blocks.SelectMany(b => b.Fields)
            .Where(f => !f.Config.ReadOnly && !Computed.Contains(f.Type))
            .Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        return keys.Where(k => !writable.Contains(k)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>寫入請求的結果：成功時 <c>data</c> 原樣、<c>meta.ignoredFields</c>（算得出時）；失敗交給執行器對照錯誤代碼。</summary>
    protected static McpToolOutcome Done(ServiceResult r, IReadOnlyList<string>? ignoredFields, string targetKind, string? targetId) =>
        r.IsSuccess
            ? McpToolOutcome.Ok(r.Data, new { ignoredFields }) with { TargetKind = targetKind, TargetId = targetId, ResultCount = 1 }
            : Fail(r) with { TargetKind = targetKind, TargetId = targetId };

    protected static McpToolOutcome Fail(ServiceResult r) => McpToolOutcome.From(r);
}
