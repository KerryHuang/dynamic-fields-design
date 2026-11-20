using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Localization;

namespace DynamicModules.Application.Dynamic.LocaleService.Packs;

/// <summary>預覽暫存與套用共用的序列化與雜湊（i4d1 計畫判斷 8）：預覽寫、套用讀，同一套規則。</summary>
public static class LocalePackStorage
{
    /// <summary><c>PlanJson</c>、<c>ReportJson</c> 的 JSON 設定（camelCase）。</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    public const int MaxFileName = 200;

    /// <summary>檔案的 SHA-256（十六進位小寫全長）。</summary>
    public static string FileHash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>語系列的內容指紋：<c>Code</c>、<c>Origin</c>、<c>NativeName</c>、<c>QuasarLang</c>、<c>FallbackCode</c> 以 U+001F 接起來的 SHA-256；比內容不比修改時間。</summary>
    public static string Fingerprint(InstalledLocale row) =>
        FileHash(Encoding.UTF8.GetBytes(string.Join('\u001f', row.Code, row.Origin.ToString(), row.NativeName, row.QuasarLang ?? "", row.FallbackCode ?? "")));

    /// <summary>只取最後一段（去掉路徑）、截到 200 字；含控制字元就存空字串。</summary>
    public static string SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var last = name.Replace('\\', '/').Split('/').Last().Trim();
        if (last.Length > MaxFileName) last = last[..MaxFileName];
        return LocaleTextSafety.Check(last, MaxFileName) is null ? last : "";
    }
}
