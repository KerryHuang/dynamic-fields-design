namespace DynamicModules.Domain.Localization;

/// <summary>
/// 上層語系圖的完整性守衛（語系包設計 §4.1、§14）：目標語系的上層必須已安裝、不能是自己、不成環；
/// 套用後**每個**已安裝語系的上層鏈都不超過 <see cref="LocaleChain.MaxParentDepth"/> 層（改掉某語系的上層可能讓它的下游超過）。
/// </summary>
public static class LocaleGraphRules
{
    public const string FallbackNotInstalled = "fallbackNotInstalled", FallbackSelf = "fallbackSelf", FallbackCycle = "fallbackCycle", FallbackTooDeep = "fallbackTooDeep";

    /// <param name="installedAfter">套用後的已安裝語系與各自的上層（含 <paramref name="target"/>）。</param>
    /// <param name="target">這次新增或改上層的語系。</param>
    public static string? Check(IReadOnlyList<(string Code, string? FallbackCode)> installedAfter, string target)
    {
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (code, fallback) in installedAfter) parents[code] = fallback;

        if (parents.TryGetValue(target, out var own) && own is not null)
        {
            if (own == target) return FallbackSelf;
            if (!parents.ContainsKey(own)) return FallbackNotInstalled;
        }

        foreach (var code in parents.Keys)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { code };
            var cur = code;
            var depth = 0;
            while (parents.TryGetValue(cur, out var parent) && parent is not null && parents.ContainsKey(parent))
            {
                if (!seen.Add(parent)) return FallbackCycle;
                if (++depth > LocaleChain.MaxParentDepth) return FallbackTooDeep;
                cur = parent;
            }
        }
        return null;
    }
}
