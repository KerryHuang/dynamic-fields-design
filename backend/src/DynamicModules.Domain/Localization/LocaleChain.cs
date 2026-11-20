using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Localization;

/// <summary>
/// 多語文字的退回鏈（語系包設計 §8.3、§10.1，U3）：要求的語系 → 上層語系（最多 <see cref="MaxParentDepth"/> 層，遇到環或沒安裝就停）
/// → 系統預設語系 → 繁中 → 全部已安裝語系（依安裝順序），去重保留第一次出現的位置。前端用同一份共用案例（<c>shared/locale-chain-cases.json</c>）驗同一條鏈。
/// </summary>
public static class LocaleChain
{
    public const int MaxParentDepth = 3;

    /// <summary>要求的語系之後的那幾步（不特地剔除要求的語系本身）。預設語系與繁中沒安裝也照放。</summary>
    public static IReadOnlyList<string> Fallbacks(string locale, string defaultLocale, IReadOnlyList<(string Code, string? FallbackCode)> installed)
    {
        var parents = installed.ToDictionary(l => l.Code, l => l.FallbackCode, StringComparer.Ordinal);
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { locale };
        var cur = locale;
        for (var depth = 0; depth < MaxParentDepth; depth++)
        {
            if (!parents.TryGetValue(cur, out var parent) || parent is null || !parents.ContainsKey(parent) || !seen.Add(parent)) break;
            chain.Add(parent);
            cur = parent;
        }
        chain.Add(defaultLocale);
        chain.Add(I18nText.FallbackLocale);
        chain.AddRange(installed.Select(l => l.Code));
        return chain.Distinct(StringComparer.Ordinal).ToList();
    }
}
