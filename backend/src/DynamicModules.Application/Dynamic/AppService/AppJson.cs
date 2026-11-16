using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Application.Dynamic.TemplateService.Data;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>讀進來的套件：v1（範本格式）或 v2；失敗時只有 Errors。</summary>
public record AppParseResult(int? FormatVersion, AppPackageData? V2, TemplatePackage? V1, IReadOnlyList<string> Errors)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// 套件檔的讀取（檔案層與結構層的一部分）：大小、UTF-8、BOM、深度、重複屬性、版本分派、未知屬性拒絕。
/// 屬性只有 <c>x-</c> 開頭的會被丟掉（頂層與 manifest），內容永遠不被解讀。
/// </summary>
public static class AppJson
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxDepth = 32;

    /// <summary>v2 的讀取設定：同範本的轉換器，但屬性名稱區分大小寫、未知屬性拒絕。</summary>
    public static readonly JsonSerializerOptions StrictOptions = new(TemplateJson.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        MaxDepth = MaxDepth
    };

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    public static AppParseResult Parse(byte[] content)
    {
        if (content.Length > MaxBytes) return Fail($"套件檔案不能超過 {MaxBytes / 1024 / 1024} MiB");

        var bytes = content.AsSpan();
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Fail("套件必須是 UTF-8 編碼");
        }

        var bytesForReader = Encoding.UTF8.GetBytes(text);
        if (FindDuplicateOrSyntaxError(bytesForReader) is { } scanError) return Fail(scanError);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(bytesForReader, nodeOptions: null, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException e)
        {
            return Fail($"套件不是有效的 JSON：{e.Message}");
        }
        if (root is not JsonObject obj) return Fail("套件必須是 JSON 物件");

        if (obj["formatVersion"] is not JsonValue fv || !fv.TryGetValue<int>(out var version))
            return Fail("缺少 formatVersion（整數）");

        if (version == 1)
        {
            var (template, error) = TemplateJson.Parse(text);
            return template is null ? Fail(error ?? "範本格式錯誤") : new AppParseResult(1, null, template, []);
        }
        if (version > AppPackageData.CurrentFormatVersion) return Fail("這個套件需要較新版本的 Formwright");
        if (version < AppPackageData.MinFormatVersion) return Fail($"不支援的 formatVersion：{version}");

        DropExtensionProperties(obj);
        if (obj["manifest"] is JsonObject manifest) DropExtensionProperties(manifest);

        var missing = new List<string>();
        foreach (var name in new[] { "format", "manifest", "modules" })
            if (obj[name] is null) missing.Add(name);
        if (obj["manifest"] is JsonObject m)
            foreach (var name in new[] { "id", "version", "name" })
                if (m[name] is null) missing.Add($"manifest.{name}");
        if (missing.Count > 0) return Fail($"缺少必要屬性：{string.Join("、", missing)}");

        try
        {
            var package = obj.Deserialize<AppPackageData>(StrictOptions);
            return package is null ? Fail("套件內容是空的") : new AppParseResult(version, package, null, []);
        }
        catch (JsonException e)
        {
            return Fail($"套件格式錯誤：{e.Message}");
        }
    }

    public static AppParseResult Parse(string json) => Parse(Encoding.UTF8.GetBytes(json));

    private static void DropExtensionProperties(JsonObject obj)
    {
        foreach (var name in obj.Select(p => p.Key).Where(k => k.StartsWith("x-", StringComparison.Ordinal)).ToList())
            obj.Remove(name);
    }

    /// <summary>System.Text.Json 預設讓後面的重複屬性覆蓋前面的，所以要自己掃一遍；順便抓語法與深度錯誤。</summary>
    private static string? FindDuplicateOrSyntaxError(byte[] utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = MaxDepth });
        var scopes = new Stack<HashSet<string>?>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        scopes.Push([]);
                        break;
                    case JsonTokenType.StartArray:
                        scopes.Push(null);
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        scopes.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        var name = reader.GetString()!;
                        if (scopes.Peek() is { } seen && !seen.Add(name))
                            return $"重複的屬性：{name}";
                        break;
                }
            }
        }
        catch (JsonException e)
        {
            return $"套件不是有效的 JSON：{e.Message}";
        }
        return null;
    }

    private static AppParseResult Fail(string error) => new(null, null, null, [error]);
}
