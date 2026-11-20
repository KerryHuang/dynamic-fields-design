using DynamicModules.Application.Common.Localization;
using DynamicModules.Domain.Localization;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.LocaleService.Packs;

/// <summary>
/// 從 <see cref="LocaleWorld"/> 取一句的文字（i4d2 計畫判斷 10）：分析器的是／否撞字與樣板的原文、參考共用。
/// 段名 <see cref="Ui"/>、<see cref="Server"/> 也是報告與清單裡 <c>segment</c> 的值。
/// </summary>
public static class LocaleWorldText
{
    public const string Ui = "ui", Server = "server";

    /// <summary>語系自己的退回鏈：自己 → 上層 → 預設語系 → 繁中 → 其他已安裝（<see cref="LocaleChain.Fallbacks"/> 不含自己，先 Prepend）。</summary>
    internal static IReadOnlyList<string> ChainOf(LocaleWorld world, string code)
    {
        var pairs = world.Installed.Select(i => (i.Code, i.FallbackCode)).ToList();
        return LocaleChain.Fallbacks(code, world.DefaultLocale, pairs).Prepend(code).Append(I18nText.FallbackLocale)
            .Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>沿鏈取第一個有值的層（每個語系先覆寫層、再內建層）；<c>Own</c>＝取自語系自己（鏈的第一個）。</summary>
    internal static (string Text, bool Own) Resolve(LocaleWorld world, IReadOnlyList<string> chain, string segment, string key)
    {
        for (var i = 0; i < chain.Count; i++)
            if (OwnText(world, chain[i], segment, key) is { } t) return (t, i == 0);
        return ("", false);
    }

    /// <summary>語系自己的句子（覆寫層 → 該語系內建層，不走退回鏈）；沒有非空值回 null。</summary>
    internal static string? OwnText(LocaleWorld world, string code, string segment, string key)
    {
        var overlay = world.Overlays.GetValueOrDefault(code);
        if (segment == Ui)
        {
            if (overlay?.Ui.GetValueOrDefault(key) is { Length: > 0 } o) return o;
            return BuiltinLocaleResources.Ui.GetValueOrDefault(code)?.GetValueOrDefault(key) is { Length: > 0 } b ? b : null;
        }
        if (overlay?.Server.GetValueOrDefault(key) is { Length: > 0 } so) return so;
        return BuiltinServerText.TryGet(code, key, out var t) && t.Length > 0 ? t : null;
    }
}
