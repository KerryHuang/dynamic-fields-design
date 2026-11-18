using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Common.Audit;

/// <summary>
/// 稽核摘要（維運設計 §17.2.1「摘要與遮蔽」）：由呼叫端逐項組出，不自動序列化請求物件。
/// 輸出前遮蔽：鍵名是 password、token、secret 的值，以及以 <c>fwp_</c>、<c>fwc_</c> 開頭的字串值一律換成 <c>***</c>（§10.2.2）。
/// 序列化後超過 <see cref="AuditLogEntry.SummaryMax"/> 字元時，差異只留變動的鍵並加 <c>"truncated": true</c>；仍超過就由大到小拿掉項目。
/// </summary>
public sealed class AuditSummary
{
    public const string Masked = "***";
    private static readonly string[] MaskedKeys = ["password", "token", "secret"];
    private static readonly string[] MaskedPrefixes = ["fwp_", "fwc_"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly JsonObject _root = new();
    private JsonNode? _before, _after;

    public bool IsEmpty => _root.Count == 0 && _before is null && _after is null;

    /// <summary>新舊值（物件的每個屬性是一個鍵）；新增時 before 為 null、刪除時 after 為 null。</summary>
    public AuditSummary Diff(object? before, object? after)
    {
        _before = ToNode(before);
        _after = ToNode(after);
        return this;
    }

    /// <summary>其他項目（變更種類、變動的格子、刪除筆數…）。</summary>
    public AuditSummary Add(string key, object? value)
    {
        _root[key] = ToNode(value);
        return this;
    }

    public string? ToJson(int max = AuditLogEntry.SummaryMax)
    {
        if (IsEmpty) return null;
        var full = Compose(_before?.DeepClone(), _after?.DeepClone());
        var text = Serialize(full);
        if (text.Length <= max) return text;

        // 差異只留變動的鍵
        var (before, after) = ChangedOnly(_before, _after);
        var cut = Compose(before, after);
        cut["truncated"] = true;
        text = Serialize(cut);
        // 仍超過：由大到小拿掉項目（保留 truncated）
        while (text.Length > max)
        {
            var largest = cut.Where(p => p.Key != "truncated")
                .OrderByDescending(p => p.Value?.ToJsonString().Length ?? 0).Select(p => p.Key).FirstOrDefault();
            if (largest is null) break;
            cut.Remove(largest);
            text = Serialize(cut);
        }
        return text;
    }

    private JsonObject Compose(JsonNode? before, JsonNode? after)
    {
        var o = new JsonObject();
        foreach (var (k, v) in _root) o[k] = v?.DeepClone();
        if (_before is not null || _after is not null)
        {
            o["before"] = before;
            o["after"] = after;
        }
        return o;
    }

    private static (JsonNode? Before, JsonNode? After) ChangedOnly(JsonNode? before, JsonNode? after)
    {
        if (before is not JsonObject b || after is not JsonObject a) return (before?.DeepClone(), after?.DeepClone());
        var nb = new JsonObject();
        var na = new JsonObject();
        foreach (var key in b.Select(p => p.Key).Union(a.Select(p => p.Key)))
        {
            var bv = b[key];
            var av = a[key];
            if (JsonNode.DeepEquals(bv, av)) continue;
            nb[key] = bv?.DeepClone();
            na[key] = av?.DeepClone();
        }
        return (nb, na);
    }

    private static JsonNode? ToNode(object? value) => value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), Json);

    private static string Serialize(JsonObject o)
    {
        Mask(o);
        return o.ToJsonString(Json);
    }

    /// <summary>就地遮蔽（給測試與其他寫入點共用）。</summary>
    public static void Mask(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    if (MaskedKeys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (o[key] is not null) o[key] = Masked;
                    }
                    else if (IsSecretValue(o[key])) o[key] = Masked;
                    else Mask(o[key]);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (IsSecretValue(a[i])) a[i] = Masked;
                    else Mask(a[i]);
                }
                break;
        }
    }

    private static bool IsSecretValue(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && MaskedPrefixes.Any(p => s.StartsWith(p, StringComparison.Ordinal));
}
