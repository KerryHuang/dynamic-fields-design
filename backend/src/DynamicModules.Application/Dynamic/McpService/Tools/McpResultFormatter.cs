using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DynamicModules.Domain.Entities.Dynamic;

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
