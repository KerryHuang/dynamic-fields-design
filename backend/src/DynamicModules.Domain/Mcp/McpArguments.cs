using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DynamicModules.Domain.Mcp;

/// <summary>
/// 工具參數的正規形與雜湊（稽核的 <c>ArgumentsHash</c>、確認碼綁參數，設計 §6.3）：物件鍵依 Ordinal 排序、去空白、拿掉頂層
/// <c>confirmationCode</c>。數字照 JSON 原字、不正規化（<c>1</c> 與 <c>1.0</c> 是不同參數）。沒有參數（<c>Undefined</c>）視同 <c>{}</c>。
/// </summary>
public static class McpArguments
{
    public const string ConfirmationCodeKey = "confirmationCode";

    public static string Canonical(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Undefined) return "{}";
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
            Write(w, args, topLevel: true);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string Hash(JsonElement args) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(args)))).ToLowerInvariant();

    private static void Write(Utf8JsonWriter w, JsonElement e, bool topLevel)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (topLevel && p.Name == ConfirmationCodeKey) continue;
                    w.WritePropertyName(p.Name);
                    Write(w, p.Value, topLevel: false);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in e.EnumerateArray()) Write(w, item, topLevel: false);
                w.WriteEndArray();
                break;
            case JsonValueKind.String:
                w.WriteStringValue(e.GetString());
                break;
            default:   // 數字、true、false、null：照原字
                w.WriteRawValue(e.GetRawText());
                break;
        }
    }
}
