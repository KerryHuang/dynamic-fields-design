using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Domain.Mcp;

namespace DynamicModules.Application.Dynamic.McpService;

/// <summary>
/// 工具呼叫稽核摘要的參數部分（mcp1a 計畫判斷 17）：保留結構與代碼、拿掉欄位值。純函式。
/// <c>filter</c> 條件樹每個節點只留 <c>field</c>、<c>op</c>、<c>fieldRef</c>，<c>value</c> 換成 <c>"…"</c>；<c>keyword</c> 只記長度；
/// 頂層 <c>confirmationCode</c> 拿掉。序列化後超過 <see cref="McpLimits.AuditArgsMax"/> 字時只留參數名稱，仍是合法 JSON。
/// </summary>
public static class McpAuditSummary
{
    /// <summary>client 名稱、版本與任務標籤的字數上限（不可信，只為辨識）。</summary>
    public const int ClientMax = 100;
    public const string Elided = "…";

    public static JsonNode Args(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return new JsonObject { ["invalid"] = args.ValueKind.ToString() };
        var o = new JsonObject();
        foreach (var p in args.EnumerateObject())
        {
            if (p.Name == McpArguments.ConfirmationCodeKey) continue;
            o[p.Name] = p.Name switch
            {
                "filter" => Filter(p.Value),
                "keyword" => new JsonObject { ["length"] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()!.Length : 0 },
                _ => JsonNode.Parse(p.Value.GetRawText()),
            };
        }
        if (o.ToJsonString().Length <= McpLimits.AuditArgsMax) return o;
        var keys = new JsonObject { ["truncated"] = true, ["keys"] = new JsonArray([.. o.Select(kv => (JsonNode?)Cut(kv.Key, ClientMax))]) };
        return keys.ToJsonString().Length <= McpLimits.AuditArgsMax ? keys : new JsonObject { ["truncated"] = true };
    }

    private static JsonNode? Filter(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return Elided;
        var o = new JsonObject();
        foreach (var p in node.EnumerateObject())
            o[p.Name] = p.Name switch
            {
                "all" or "any" when p.Value.ValueKind == JsonValueKind.Array => new JsonArray([.. p.Value.EnumerateArray().Select(Filter)]),
                "field" or "op" or "fieldRef" => JsonNode.Parse(p.Value.GetRawText()),
                _ => Elided,
            };
        return o;
    }

    public static string? Cut(string? value, int max = ClientMax) => value is { Length: var n } && n > max ? value[..max] : value;
}
