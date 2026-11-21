using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;

namespace DynamicModules.Application.Dynamic.McpService.Tools;

/// <summary>
/// 格式化好、可以送出的工具結果：<see cref="Text"/> 是文字內容（第一行不可信提醒、第二行起 JSON），<see cref="Structured"/> 是同一份 JSON。
/// <see cref="Size"/> 是兩者合計的字元數（判斷 14：無法確認 client 只計哪一個，合計最保守）。
/// </summary>
public sealed record McpFormattedResult(bool IsError, string Text, JsonElement Structured, string? ErrorCode)
{
    public int Size => Text.Length + Structured.GetRawText().Length;
}

/// <summary>
/// 工具結果格式（設計 §9.1、§6.5；mcp1a 計畫判斷 12）。純函式：
/// 成功是 <c>{ contentTrust, meta: { tool, … }, data }</c>；錯誤是 <c>{ contentTrust, error: { code, message, fields?, retryAfterSeconds? } }</c>。
/// JSON 是 camelCase，enum 轉換與 controller 相同。
/// </summary>
public static class McpResultFormatter
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new LowercaseEnumConverter<FieldType>(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static McpFormattedResult Success(string tool, object? meta, object? data)
    {
        var metaNode = new JsonObject { ["tool"] = tool };
        if (ToNode(meta) is JsonObject extra)
            foreach (var (k, v) in extra.ToList())
            {
                extra.Remove(k);
                metaNode[k] = v;
            }
        var root = new JsonObject { ["contentTrust"] = McpTexts.ContentTrust, ["meta"] = metaNode, ["data"] = ToNode(data) };
        return Build(false, root, null);
    }

    /// <summary>
    /// 成功結果加大小判斷（判斷 14；寫死清單：<see cref="McpLimits.MaxResultChars"/> 是文字內容加結構化內容合計）。放不下時：
    /// 分頁列表不截斷，回 <c>result_too_large</c> 與 <c>retry.pageSize</c>（這一頁能放下的最多筆數；1 筆仍放不下就不帶）；
    /// 報表明細截到放得下；其餘（單筆、分組報表）回 <c>result_too_large</c>，有提示就附 <c>hint</c>。
    /// </summary>
    public static McpFormattedResult Fit(string tool, McpToolOutcome outcome, string tooLargeMessage, int max = McpLimits.MaxResultChars)
    {
        var full = Success(tool, outcome.Meta, outcome.Data);
        if (full.Size <= max) return full;
        if (outcome.Resize is { Count: > 1 } resize)
        {
            McpFormattedResult Take(int n)
            {
                var (meta, data) = resize.Take(n);
                return Success(tool, meta, data);
            }
            // 項目越少結果越小：二分找放得下的最多筆數
            int lo = 0, hi = resize.Count - 1;
            McpFormattedResult? best = null;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                var candidate = Take(mid);
                if (candidate.Size <= max) { lo = mid; best = candidate; }
                else hi = mid - 1;
            }
            if (lo >= 1)
            {
                best ??= Take(lo);
                return resize.Mode == McpOversize.Truncate
                    ? best
                    : Error(McpErrorCodes.ResultTooLarge, tooLargeMessage, extra: new { retry = new { pageSize = lo } });
            }
        }
        return Error(McpErrorCodes.ResultTooLarge, tooLargeMessage, extra: outcome.OversizeHint is { } hint ? new { hint } : null);
    }

    /// <summary>
    /// 確認已提交、但結果回不了（判斷 18）：非錯誤的固定結果，沒有 <c>data</c>；
    /// <c>meta = { tool, confirmation: { status: "executed", id }, dataOmitted, instruction }</c>。
    /// </summary>
    public static McpFormattedResult Executed(string tool, long? confirmationId, string dataOmitted, string instruction)
    {
        var meta = new JsonObject
        {
            ["tool"] = tool, ["confirmation"] = new JsonObject { ["status"] = "executed", ["id"] = confirmationId },
            ["dataOmitted"] = dataOmitted, ["instruction"] = instruction,
        };
        return Build(false, new JsonObject { ["contentTrust"] = McpTexts.ContentTrust, ["meta"] = meta }, null);
    }

    /// <summary>錯誤結果；訊息已依請求語系解析。</summary>
    public static McpFormattedResult Error(string code, string message, IReadOnlyDictionary<string, string>? fields = null,
        int? retryAfterSeconds = null, object? extra = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (fields is { Count: > 0 }) error["fields"] = ToNode(fields);
        if (retryAfterSeconds is { } s) error["retryAfterSeconds"] = s;
        if (ToNode(extra) is JsonObject more)
            foreach (var (k, v) in more.ToList())
            {
                more.Remove(k);
                error[k] = v;
            }
        var root = new JsonObject { ["contentTrust"] = McpTexts.ContentTrust, ["error"] = error };
        return Build(true, root, code);
    }

    private static McpFormattedResult Build(bool isError, JsonObject root, string? code)
    {
        var json = root.ToJsonString(Json);
        return new McpFormattedResult(isError, McpTexts.UntrustedNotice + "\n" + json, JsonDocument.Parse(json).RootElement.Clone(), code);
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement e => JsonNode.Parse(e.GetRawText()),
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), Json),
    };
}
