using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Domain.Mcp;

namespace DynamicModules.Application.Dynamic.McpService.Tools;

/// <summary>
/// 一個 MCP 工具（mcp1a 計畫判斷 19）。工具類別的建構函式只收 <c>ISender</c>／<c>IMediator</c>（架構測試鎖住，T4）：
/// 資料一律經既有 request 取得，權限全走既有 behavior 與 guard。名稱、描述、參數說明是固定英文（<see cref="McpTexts"/>）。
/// </summary>
public interface IMcpTool
{
    McpToolDescriptor Descriptor { get; }
    /// <summary>固定的 JSON Schema 字串（英文說明）。</summary>
    string InputSchemaJson { get; }
    /// <summary>固定英文描述。</summary>
    string Description { get; }
    Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct);
}

/// <summary>一次工具呼叫的輸入：參數（沒帶是空物件）、當次讀到的政策快照、請求語系。</summary>
public sealed record McpToolCall(JsonElement Arguments, McpPolicySnapshot Limits, string Locale);

/// <summary>
/// 工具執行的結果，還沒格式化。成功時 <see cref="Meta"/>／<see cref="Data"/> 分區；handler 回的失敗放 <see cref="Failure"/>，
/// 由 invoker 依 §6.5 對照成錯誤代碼（工具拿不到訊息目錄）。<see cref="TargetKind"/> 以下是稽核欄位。
/// 結果放不下時（判斷 14）：沒有 <see cref="Resize"/> 的單筆結果回 <c>result_too_large</c>（可附 <see cref="OversizeHint"/>）；
/// 分頁列表回建議的 <c>pageSize</c>；報表明細截斷。
/// </summary>
public sealed record McpToolOutcome(bool IsError, object? Meta, object? Data, string? ErrorCode = null, MessageRef? ErrorMessage = null,
    IReadOnlyDictionary<string, MessageRef>? Fields = null, int? RetryAfterSeconds = null,
    string? TargetKind = null, string? TargetId = null, int? ResultCount = null, IReadOnlyList<long>? RecordIds = null,
    ServiceResult? Failure = null, McpResize? Resize = null, string? OversizeHint = null)
{
    public static McpToolOutcome Ok(object? data, object? meta = null) => new(false, meta, data);

    public static McpToolOutcome Error(string code, MessageRef message, IReadOnlyDictionary<string, MessageRef>? fields = null,
        int? retryAfterSeconds = null) => new(true, null, null, code, message, fields, retryAfterSeconds);

    /// <summary>handler 回的失敗結果；錯誤代碼與訊息由 invoker 決定。</summary>
    public static McpToolOutcome From(ServiceResult failure) => new(true, null, null, Failure: failure);

    public static McpToolOutcome InvalidArguments(IReadOnlyDictionary<string, MessageRef> fields) =>
        Error(McpErrorCodes.InvalidArguments, fields.Values.First(), fields);
}

/// <summary>結果放不下時的處理：分頁列表回建議筆數（不截斷，避免下一頁跳過被拿掉的筆）；報表明細截到放得下。</summary>
public enum McpOversize { SuggestPageSize, Truncate }

/// <summary>
/// 結果的可縮小形式：<see cref="Count"/> 是目前的項目數，<see cref="Take"/>(n) 回只含前 n 項的 meta 與 data
/// （報表用 <c>ReportRules.TakeDetail</c>，並在 meta 標 <c>truncated</c>、<c>rowsReturned</c>、<c>rowsTotal</c>）。
/// </summary>
public sealed record McpResize(McpOversize Mode, int Count, Func<int, (object? Meta, object? Data)> Take);

/// <summary>工具結果的錯誤代碼（設計 §6.5、計畫判斷 12）：協定上的機器碼，給模型判斷能不能修正。</summary>
public static class McpErrorCodes
{
    public const string Stale = "stale", Invalid = "invalid", Forbidden = "forbidden", NotFound = "not_found", RateLimited = "rate_limited",
        ConfirmationInvalid = "confirmation_invalid", AuditUnavailable = "audit_unavailable", ResultTooLarge = "result_too_large",
        UnknownTool = "unknown_tool", InvalidArguments = "invalid_arguments", ScopeDenied = "scope_denied", InternalError = "internal_error";
}
