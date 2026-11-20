using System.Text.Json;

namespace DynamicModules.Domain.Localization;

/// <summary>
/// 介面文字的純函式（語系包設計 §8.2；i4b 計畫判斷 5、6）：巢狀 JSON 壓平成「a.b.c → 句子」。
/// </summary>
public static class UiTextMerge
{
    /// <summary>巢狀物件壓平成 <c>a.b.c</c> 鍵；根不是物件、或值不是字串或物件（陣列、數字、null）就丟 <see cref="FormatException"/>。</summary>
    public static IReadOnlyDictionary<string, string> Flatten(JsonElement nested)
    {
        if (nested.ValueKind != JsonValueKind.Object) throw new FormatException("介面文字的根必須是物件");
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(nested, "", flat);
        return flat;
    }

    private static void Walk(JsonElement obj, string prefix, Dictionary<string, string> flat)
    {
        foreach (var p in obj.EnumerateObject())
        {
            var key = prefix.Length == 0 ? p.Name : $"{prefix}.{p.Name}";
            switch (p.Value.ValueKind)
            {
                case JsonValueKind.String: flat[key] = p.Value.GetString()!; break;
                case JsonValueKind.Object: Walk(p.Value, key, flat); break;
                default: throw new FormatException($"「{key}」的值不是字串或物件");
            }
        }
    }
}
