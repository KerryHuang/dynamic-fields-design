using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Domain.Mcp;

namespace DynamicModules.Application.Dynamic.McpService;

/// <summary>
/// 工具呼叫稽核摘要的參數部分（mcp1a 計畫判斷 17）：保留結構與代碼、拿掉欄位值。純函式。
/// <c>filter</c> 條件樹每個節點只留 <c>field</c>、<c>op</c>、<c>fieldRef</c>，<c>value</c> 換成 <c>"…"</c>；<c>keyword</c> 只記長度；
/// 頂層 <c>confirmationCode</c> 拿掉。序列化後超過 <see cref="McpLimits.AuditArgsMax"/> 字時只留參數名稱，仍是合法 JSON。
/// <para>
/// strict 模式（寫入、高風險、不存在的工具，mcp2a 判斷 15）逐參數去值：<c>values</c> 只留排序後的鍵；<c>details</c> 只留明細模組 key、
/// 每列有無 <c>id</c> 與鍵；<c>title</c>／<c>description</c>／<c>body</c>／<c>comment</c> 只記長度；<c>picks</c> 只留代碼與人數；
/// <c>dueDate</c>／<c>startDate</c> 只記有或清除；id、版本、代碼類參數（<see cref="Kept"/>）只有純量且不超過 <see cref="KeptMax"/> 字才存原值；
/// 其餘的鍵、型別不對的值一律只記型別與長度。
/// </para>
/// <para>所有鍵名（頂層參數、<c>values</c>、明細模組與 <c>other</c>、<c>picks</c>、條件樹）都截到 <see cref="ClientMax"/> 字：鍵名是 AI 給的，可能夾帶值。</para>
/// </summary>
public static class McpAuditSummary
{
    /// <summary>client 名稱、版本與任務標籤的字數上限（不可信，只為辨識）。</summary>
    public const int ClientMax = 100;
    public const string Elided = "…";

    /// <summary>strict 模式下保留原值的參數（id、版本、代碼、旗標；不是自由文字）。</summary>
    public static readonly IReadOnlySet<string> Kept = new HashSet<string>(StringComparer.Ordinal)
    {
        "idempotencyKey", "recordId", "taskId", "parentTaskId", "instanceId", "assigneeUserId", "rowVersion", "status", "cascade", "to",
        "previewOnly", "moduleKey", "hostKey",
    };

    /// <summary>保留原值的字數上限（技術限制）：超過只記型別與長度。</summary>
    public const int KeptMax = 100;

    public static JsonNode Args(JsonElement args, bool strict = false)
    {
        if (args.ValueKind != JsonValueKind.Object) return new JsonObject { ["invalid"] = args.ValueKind.ToString() };
        var o = new JsonObject();
        foreach (var p in args.EnumerateObject())
        {
            if (p.Name == McpArguments.ConfirmationCodeKey) continue;
            // 鍵名是 AI 給的字串（可能夾帶值），一律截到 ClientMax（mcp2a 審查建議 3）
            o[Cut(p.Name)!] = p.Name switch
            {
                "filter" => Filter(p.Value),
                "keyword" => new JsonObject { ["length"] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()!.Length : 0 },
                _ when !strict => JsonNode.Parse(p.Value.GetRawText()),
                "values" => Keys(p.Value),
                "details" => Details(p.Value),
                "title" or "description" or "body" or "comment" => p.Value.ValueKind == JsonValueKind.String
                    ? new JsonObject { ["length"] = p.Value.GetString()!.Length } : TypeOnly(p.Value),
                "picks" => Picks(p.Value),
                "dueDate" or "startDate" => p.Value.ValueKind switch
                {
                    JsonValueKind.Null => "clear",
                    JsonValueKind.String => "set",
                    _ => TypeOnly(p.Value),
                },
                _ when Kept.Contains(p.Name) && IsShortScalar(p.Value) => JsonNode.Parse(p.Value.GetRawText()),
                _ => TypeOnly(p.Value),
            };
        }
        if (o.ToJsonString().Length <= McpLimits.AuditArgsMax) return o;
        var keys = new JsonObject { ["truncated"] = true, ["keys"] = new JsonArray([.. o.Select(kv => (JsonNode?)kv.Key)]) };
        return keys.ToJsonString().Length <= McpLimits.AuditArgsMax ? keys : new JsonObject { ["truncated"] = true };
    }

    private static bool IsShortScalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()!.Length <= KeptMax,
        JsonValueKind.Number => v.GetRawText().Length <= KeptMax,
        JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
        _ => false,
    };

    /// <summary>只記型別與長度（字串是字數、陣列是項數、物件是鍵數、其他是原文長度），不留原值。</summary>
    private static JsonObject TypeOnly(JsonElement v) => new()
    {
        ["type"] = v.ValueKind.ToString().ToLowerInvariant(),
        ["length"] = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()!.Length,
            JsonValueKind.Array => v.GetArrayLength(),
            JsonValueKind.Object => v.EnumerateObject().Count(),
            _ => v.GetRawText().Length,
        },
    };

    /// <summary>物件只留排序後的鍵；不是物件只記型別。</summary>
    private static JsonNode Keys(JsonElement v) => v.ValueKind == JsonValueKind.Object
        ? new JsonArray([.. v.EnumerateObject().Select(p => Cut(p.Name)!).Order(StringComparer.Ordinal).Select(n => (JsonNode?)n)])
        : TypeOnly(v);

    /// <summary>明細：模組 key → 每列 <c>{ id: 有無, keys: values 的鍵 }</c>；不是物件、不是陣列、列不是物件都只記型別。</summary>
    private static JsonNode Details(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object) return TypeOnly(v);
        var o = new JsonObject();
        foreach (var m in v.EnumerateObject())
            o[Cut(m.Name)!] = m.Value.ValueKind != JsonValueKind.Array ? TypeOnly(m.Value)
                : new JsonArray([.. m.Value.EnumerateArray().Select(Row)]);
        return o;

        static JsonNode? Row(JsonElement row)
        {
            if (row.ValueKind != JsonValueKind.Object) return TypeOnly(row);
            var r = new JsonObject { ["id"] = row.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null };
            if (row.TryGetProperty("values", out var values)) r["keys"] = Keys(values);
            var other = row.EnumerateObject().Select(p => p.Name).Where(n => n is not ("id" or "values" or "rowVersion"))
                .Select(n => Cut(n)!).Order(StringComparer.Ordinal).ToList();
            if (other.Count > 0) r["other"] = new JsonArray([.. other.Select(n => (JsonNode?)n)]);
            return r;
        }
    }

    /// <summary>自選關卡：代號 → 人數；不是物件、值不是陣列只記型別。</summary>
    private static JsonNode Picks(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object) return TypeOnly(v);
        var o = new JsonObject();
        foreach (var p in v.EnumerateObject())
            o[Cut(p.Name)!] = p.Value.ValueKind == JsonValueKind.Array ? p.Value.GetArrayLength() : TypeOnly(p.Value);
        return o;
    }

    private static JsonNode? Filter(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return Elided;
        var o = new JsonObject();
        foreach (var p in node.EnumerateObject())
            o[Cut(p.Name)!] = p.Name switch
            {
                "all" or "any" when p.Value.ValueKind == JsonValueKind.Array => new JsonArray([.. p.Value.EnumerateArray().Select(Filter)]),
                "field" or "op" or "fieldRef" => JsonNode.Parse(p.Value.GetRawText()),
                _ => Elided,
            };
        return o;
    }

    public static string? Cut(string? value, int max = ClientMax) => value is { Length: var n } && n > max ? value[..max] : value;
}
