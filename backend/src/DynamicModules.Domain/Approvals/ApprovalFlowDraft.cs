using System.Text.Json;

namespace DynamicModules.Domain.Approvals;

/// <summary>
/// 設計器的草稿（流程圖設計 §10.8、J65），存在 ApprovalFlow.DraftJson：{ "rev", "definition", "layout" }。
/// Rev 是每次儲存新產生的隨機權杖（不是計數），捨棄後別人再存也不會撞回同一個值。
/// </summary>
public sealed record ApprovalFlowDraft(string Rev, ApprovalDefinition Definition, ApprovalFlowLayout? Layout)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static string NewRev() => Guid.NewGuid().ToString("N");

    public string Serialize() => JsonSerializer.Serialize(new { rev = Rev, definition = Definition, layout = Layout }, Web);

    /// <summary>讀不懂（舊格式沒有 rev、JSON 壞掉、definition 反序列化失敗）回 null，不丟例外。</summary>
    public static ApprovalFlowDraft? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("rev", out var rev) || rev.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(rev.GetString())
                || !root.TryGetProperty("definition", out var def) || def.ValueKind != JsonValueKind.Object) return null;
            var definition = def.Deserialize<ApprovalDefinition>(Web);
            if (definition is null) return null;
            var layout = root.TryGetProperty("layout", out var l) && l.ValueKind == JsonValueKind.Object ? ApprovalFlowLayout.Parse(l) : null;
            return new ApprovalFlowDraft(rev.GetString()!, definition, layout);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException) { return null; }
    }
}
