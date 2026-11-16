using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.PrintService;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// 列印範本內文在套件（欄位代號、模組代號）與資料庫（f_&lt;欄位 Id&gt;、Module.Key）之間互轉。
/// 佔位符依所在位置決定欄位屬於哪個模組：迴圈外是範本所屬模組，迴圈內是該迴圈的明細模組。
/// 解析不了的欄位佔位符直接拿掉（印出來是空的，和停用欄位一致）並記下來；解析不了的迴圈整份範本不轉。
/// </summary>
public static partial class AppPrintCodec
{
    [GeneratedRegex(@"\{\{#each\s+([A-Za-z0-9_-]+)\s*\}\}|\{\{(/each)\}\}|\{\{\s*([A-Za-z0-9_]+)\s*\}\}")]
    private static partial Regex Token();

    [GeneratedRegex(@"^f_(\d+)$")]
    private static partial Regex DbField();

    public sealed record Converted(string? Body, List<string> Dropped);

    /// <param name="moduleKeyOf">模組代號 → Module.Key；null 代表不在這次開啟的範圍。</param>
    /// <param name="fieldIdOf">限定欄位名（模組代號.欄位代號）→ 欄位 Id；null 代表被排除。</param>
    public static Converted ToDatabase(string body, string module, Func<string, string?> moduleKeyOf, Func<string, long?> fieldIdOf)
    {
        var dropped = new List<string>();
        var current = module;
        var unresolvedLoop = false;
        var result = Token().Replace(body, m =>
        {
            if (m.Groups[1].Success)
            {
                var key = moduleKeyOf(m.Groups[1].Value);
                if (key is null) { unresolvedLoop = true; return m.Value; }
                current = m.Groups[1].Value;
                return $"{{{{#each {key}}}}}";
            }
            if (m.Groups[2].Success) { current = module; return m.Value; }
            var slot = m.Groups[3].Value;
            if (slot.StartsWith('_')) return m.Value;
            if (fieldIdOf($"{current}.{slot}") is { } id) return $"{{{{f_{id}}}}}";
            dropped.Add($"{current}.{slot}");
            return "";
        });
        return new Converted(unresolvedLoop ? null : result, dropped);
    }

    /// <param name="moduleCodeOf">Module.Key → 模組代號；null 代表不在套件內。</param>
    /// <param name="fieldNameOf">欄位 Id → 限定欄位名；null 代表停用或未匯出。</param>
    public static Converted ToPackage(string body, string module, Func<string, string?> moduleCodeOf, Func<long, string?> fieldNameOf)
    {
        var dropped = new List<string>();
        var current = module;
        var unresolvedLoop = false;
        var result = Token().Replace(body, m =>
        {
            if (m.Groups[1].Success)
            {
                var code = moduleCodeOf(m.Groups[1].Value);
                if (code is null) { unresolvedLoop = true; return m.Value; }
                current = code;
                return $"{{{{#each {code}}}}}";
            }
            if (m.Groups[2].Success) { current = module; return m.Value; }
            var slot = m.Groups[3].Value;
            if (slot.StartsWith('_')) return m.Value;
            var match = DbField().Match(slot);
            var name = match.Success ? fieldNameOf(long.Parse(match.Groups[1].Value)) : null;
            var prefix = current + ".";
            if (name is not null && name.StartsWith(prefix, StringComparison.Ordinal)) return $"{{{{{name[prefix.Length..]}}}}}";
            dropped.Add($"{current}.{slot}");
            return "";
        });
        return new Converted(unresolvedLoop ? null : result, dropped);
    }
}
