using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.LocaleService.Packs;

/// <summary>
/// 覆寫層版本列的內容（i4d1 計畫判斷 7）：套用、回到上一版、移除覆寫共用。<c>UiJson</c>／<c>ServerJson</c> 是整份覆寫層（扁平、鍵 Ordinal 排序）；
/// <c>BaseHashesJson</c> 是 <c>{"ui":{鍵:雜湊},"server":{鍵:雜湊}}</c>，雜湊取匯入當時內建 zh-TW 原文的 SHA-256 前 16 個十六進位字元，
/// 沒有新值的鍵沿用舊雜湊。
/// </summary>
internal static class LocalePackVersions
{
    private static readonly JsonSerializerOptions Storage = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string LayerJson(IReadOnlyDictionary<string, string> layer) =>
        JsonSerializer.Serialize(new SortedDictionary<string, string>(layer.ToDictionary(), StringComparer.Ordinal), Storage);

    public static string BaseHashesJson(LocalePack? current, IReadOnlyDictionary<string, string> ui, IReadOnlyDictionary<string, string> server,
        IReadOnlyCollection<string> uiChanged, IReadOnlyCollection<string> serverChanged)
    {
        var old = ParseHashes(current?.BaseHashesJson);
        var result = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["server"] = Hashes(server.Keys, serverChanged, old.GetValueOrDefault("server"), key =>
                BuiltinServerText.TryGet(I18nText.FallbackLocale, key, out var zh) ? zh : ""),
            ["ui"] = Hashes(ui.Keys, uiChanged, old.GetValueOrDefault("ui"), key =>
                BuiltinLocaleResources.Ui[I18nText.FallbackLocale].GetValueOrDefault(key) ?? ""),
        };
        return JsonSerializer.Serialize(result, Storage);
    }

    /// <summary>空的版本（移除覆寫，Q5）。</summary>
    public static LocalePack Empty(string code, string? userId, DateTime now) => new()
    {
        Code = code, UiJson = "{}", ServerJson = "{}", BaseHashesJson = "{}", ImportedUserId = userId, ImportedUtc = now, ModifiedUtc = now,
    };

    /// <summary>上一版內容的複本（回到上一版：新的一列、新的 Id，判斷 20）。</summary>
    public static LocalePack CopyOf(LocalePack source, DateTime now) => new()
    {
        Code = source.Code, UiJson = source.UiJson, ServerJson = source.ServerJson, BaseHashesJson = source.BaseHashesJson,
        FileHash = source.FileHash, FileName = source.FileName, PackVersion = source.PackVersion, BuiltFor = source.BuiltFor,
        BaseLocale = source.BaseLocale, Note = source.Note, ReportJson = source.ReportJson,
        ImportedUserId = source.ImportedUserId, ImportedUtc = source.ImportedUtc, ModifiedUtc = now,
    };

    private static SortedDictionary<string, string> Hashes(IEnumerable<string> keys, IReadOnlyCollection<string> changed,
        IReadOnlyDictionary<string, string>? old, Func<string, string> zh)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
            hashes[key] = !changed.Contains(key) && old?.GetValueOrDefault(key) is { } kept ? kept : Hash(zh(key));
        return hashes;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    private static Dictionary<string, Dictionary<string, string>> ParseHashes(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
