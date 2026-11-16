using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicModules.Domain.FieldTypes;

/// <summary>附件欄位的值裡的一個檔案：Id 是系統產生的 32 碼十六進位（也是磁碟上的儲存名），Name 是使用者看到的原檔名。</summary>
public sealed record AttachmentRef(string Id, string Name, long Size);

/// <summary>附件欄位的值（檔案描述陣列）的讀寫。值裡永遠沒有路徑；檔案只能靠上傳端點加入。</summary>
public static partial class AttachmentValue
{
    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>讀不懂的元素略過（不丟例外）；非陣列回空清單。</summary>
    public static List<AttachmentRef> Parse(JsonElement? value)
    {
        var result = new List<AttachmentRef>();
        if (value is not { ValueKind: JsonValueKind.Array } array) return result;
        foreach (var e in array.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var id = e.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            var name = e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var size = e.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var l) ? l : 0;
            if (IsValidId(id) && name is not null) result.Add(new AttachmentRef(id!, name, size));
        }
        return result;
    }

    public static JsonElement ToJson(IEnumerable<AttachmentRef> files) =>
        JsonSerializer.SerializeToElement(files.Select(f => new { id = f.Id, name = f.Name, size = f.Size }));
}
