using System.Text.Json;

namespace DynamicModules.Domain.Approvals;

/// <summary>
/// 設計器的草稿（流程圖設計 §10.8、J65），存在 ApprovalFlow.DraftJson：{ "rev", "definition", "layout" }。
/// Rev 是每次儲存新產生的隨機權杖（不是計數），捨棄後別人再存也不會撞回同一個值。
/// 發布、捨棄與原地改定義之後不留草稿內容，只留 { "rev" }：權杖照樣換新，原地發布（CurrentVersionId 不變）才不會回到同一個權杖（ABA）。
/// 「有沒有草稿」看 <see cref="TryParse"/> 是否回非 null，不看 DraftJson 是否為 null；樂觀鎖權杖一律用 <see cref="TokenOf"/>。
/// </summary>
public sealed record ApprovalFlowDraft(string Rev, ApprovalDefinition Definition, ApprovalFlowLayout? Layout)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static string NewRev() => Guid.NewGuid().ToString("N");

    public string Serialize() => JsonSerializer.Serialize(new { rev = Rev, definition = Definition, layout = Layout }, Web);

    /// <summary>只有權杖、沒有草稿內容的 DraftJson（發布、捨棄後）。</summary>
    public static string TokenOnly(string rev) => JsonSerializer.Serialize(new { rev }, Web);

    /// <summary>樂觀鎖權杖：有沒有草稿內容都回 rev；沒有 DraftJson、或讀不出 rev（舊格式、壞 JSON）回 null。</summary>
    public static string? TokenOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("rev", out var rev)
                && rev.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(rev.GetString()) ? rev.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>換一個新權杖、內容不動：有草稿就保留定義與版面，沒有就留只有權杖的 DraftJson。</summary>
    public static string WithNewRev(string? json) =>
        TryParse(json) is { } draft ? (draft with { Rev = NewRev() }).Serialize() : TokenOnly(NewRev());

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
