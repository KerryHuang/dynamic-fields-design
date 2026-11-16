using System.Text;
using System.Text.Json;

namespace DynamicModules.Domain.Approvals;

public readonly record struct LayoutPoint(double X, double Y);

/// <summary>
/// 流程圖設計器的節點座標（流程圖設計 §7.5、J66）：{ "nodes": { "&lt;節點代號&gt;": { "x", "y" } } }，不存畫布視角。
/// 缺座標的節點允許（前端自動排）；不存在於定義的代號在發布時由 <see cref="Prune"/> 丟掉。純函式。
/// </summary>
public sealed record ApprovalFlowLayout(IReadOnlyDictionary<string, LayoutPoint> Nodes)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static ApprovalFlowLayout Empty => new(new Dictionary<string, LayoutPoint>());

    /// <summary>寬鬆讀取：讀不懂或座標不合法的節點略過，不丟例外（格式檢查另走 <see cref="Validate"/>）。</summary>
    public static ApprovalFlowLayout Parse(JsonElement? json)
    {
        var nodes = new Dictionary<string, LayoutPoint>();
        if (json is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("nodes", out var map) && map.ValueKind == JsonValueKind.Object)
            foreach (var p in map.EnumerateObject())
                if (ApprovalCodes.IsValid(p.Name) && Point(p.Value) is { } pt) nodes[p.Name] = pt;
        return new ApprovalFlowLayout(nodes);
    }

    public static ApprovalFlowLayout Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Parse(doc.RootElement);
        }
        catch (JsonException) { return Empty; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Web);

    /// <summary>
    /// 格式檢查：整段不超過 64 KB、nodes 是物件、節點代號合法、座標是有限數字且絕對值不超過 100000。
    /// 鍵 layout 或 layout.nodes.{代號}；null 與缺 layout 視為沒有版面，不報錯。
    /// </summary>
    public static IReadOnlyList<GraphIssue> Validate(JsonElement? json)
    {
        if (json is not { } root || root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return [];
        if (Encoding.UTF8.GetByteCount(root.GetRawText()) > ApprovalGraphLimits.MaxLayoutBytes) return [Issue("layout", "layoutTooLarge")];
        if (root.ValueKind != JsonValueKind.Object) return [Issue("layout", "layoutShape")];
        if (!root.TryGetProperty("nodes", out var map) || map.ValueKind == JsonValueKind.Null) return [];
        if (map.ValueKind != JsonValueKind.Object) return [Issue("layout", "layoutShape")];

        var issues = new List<GraphIssue>();
        foreach (var p in map.EnumerateObject())
        {
            var key = $"layout.nodes.{p.Name}";
            if (!ApprovalCodes.IsValid(p.Name)) issues.Add(Issue(key, "layoutNodeCode"));
            else if (Point(p.Value) is null) issues.Add(Issue(key, "layoutCoordinate"));
        }
        return issues;
    }

    /// <summary>只留下定義裡還存在的節點的座標。</summary>
    public static ApprovalFlowLayout Prune(ApprovalFlowLayout layout, ApprovalDefinition definition)
    {
        var ids = (definition.Nodes ?? []).Where(n => n is not null).Select(n => n.Id).ToHashSet();
        return new ApprovalFlowLayout(layout.Nodes.Where(kv => ids.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    private static GraphIssue Issue(string key, string code) => new(key, "layout", code);

    private static LayoutPoint? Point(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("x", out var x) || !e.TryGetProperty("y", out var y)) return null;
        return Coordinate(x) is { } cx && Coordinate(y) is { } cy ? new LayoutPoint(cx, cy) : null;
    }

    private static double? Coordinate(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) && double.IsFinite(v) && Math.Abs(v) <= ApprovalGraphLimits.MaxCoordinate ? v : null;
}
