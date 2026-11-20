using System.Security.Cryptography;
using System.Text.Json;

namespace DynamicModules.Domain.Localization;

/// <summary>
/// 介面文字的純函式（語系包設計 §8.2；i4b 計畫判斷 5、6）：巢狀 JSON 壓平成「a.b.c → 句子」、沿退回鏈逐鍵補齊、序列化並算內容雜湊。
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

    /// <summary>雜湊長度：SHA-256 的前 16 個十六進位字元（技術限制，i4b 計畫判斷 5）。</summary>
    public const int HashLength = 16;

    /// <summary>
    /// 逐鍵補齊：基準鍵集（內建繁中的鍵）的每個鍵取 <paramref name="layers"/> 依序第一個非空字串；基準以外的鍵不輸出。
    /// 結果依 <see cref="StringComparer.Ordinal"/> 排序（不受文化設定影響，每台伺服器、前後端都相同）。
    /// </summary>
    public static SortedDictionary<string, string> Merge(IReadOnlyList<string> baseKeys, IEnumerable<IReadOnlyDictionary<string, string>> layers)
    {
        var list = layers.ToList();
        var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in baseKeys)
        {
            foreach (var layer in list)
            {
                if (layer.TryGetValue(key, out var text) && !string.IsNullOrEmpty(text))
                {
                    merged[key] = text;
                    break;
                }
            }
        }
        return merged;
    }

    /// <summary>序列化成扁平 JSON（UTF-8、預設 encoder）並取內容雜湊：內容相同雜湊就相同，內容一變雜湊就變。</summary>
    public static (string Hash, byte[] Json) Serialize(SortedDictionary<string, string> merged)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(merged);
        return (Convert.ToHexString(SHA256.HashData(json))[..HashLength].ToLowerInvariant(), json);
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
