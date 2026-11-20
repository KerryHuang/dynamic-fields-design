using System.Globalization;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Common.Localization;

/// <summary>
/// 後端訊息目錄（singleton；語系包設計 §9.1，i4c 計畫判斷 1、2）。依賴的 <see cref="LocaleRegistry"/>（已安裝清單與上層關係）
/// 與 <see cref="CachedSystemPolicy"/>（預設語系）都是記憶體快照，查一句不碰資料庫。每個語系目前只有內建層
/// （<see cref="BuiltinServerText"/>，只認內建四語）；i4d1 的覆寫層插在每個語系的內建層前面。
/// </summary>
public sealed class MessageCatalog(LocaleRegistry registry, CachedSystemPolicy policy) : IMessageCatalog
{
    /// <summary>只給測試：<see cref="AsI18n"/> 擷取快照之後、產生每個語系之前呼叫（參數是那個語系）。</summary>
    public Action<string>? BeforeLocaleForTests { get; set; }

    public string Get(LocaleContext lc, string key, params object[] args) => Format(Lookup(lc, key), args);

    /// <summary>預設語系只讀一次、已安裝語系與每個語系的鏈取自同一份 registry 快照（判斷 2）；已停用的語系也產生（M3）。</summary>
    public I18nText AsI18n(string key, params object[] args)
    {
        var contexts = registry.ContextsFor(policy.DefaultLocale);
        var values = new List<KeyValuePair<string, string>>(contexts.Count);
        foreach (var lc in contexts)
        {
            BeforeLocaleForTests?.Invoke(lc.Locale);
            values.Add(KeyValuePair.Create(lc.Locale, Get(lc, key, args)));
        }
        return new I18nText(values);
    }

    /// <summary>沿鏈取第一個有值（非 null、非空字串）的層；全部缺值回 null（判斷 12：呼叫端對應成空字串或例外）。</summary>
    public static string? FirstInChain(IEnumerable<string> chain, Func<string, string?> layer)
    {
        foreach (var locale in chain)
            if (layer(locale) is { Length: > 0 } text) return text;
        return null;
    }

    /// <summary>[lc.Locale, ..lc.Fallbacks, zh-TW] 去重後沿鏈找內建範本；繁中內建層完整，找不到就是鍵寫錯。</summary>
    internal static string Lookup(LocaleContext lc, string key) =>
        FirstInChain(lc.Fallbacks.Prepend(lc.Locale).Append(I18nText.FallbackLocale).Distinct(StringComparer.Ordinal),
            l => BuiltinServerText.TryGet(l, key, out var t) ? t : null)
        ?? throw new KeyNotFoundException($"後端訊息沒有這個鍵：{key}");

    /// <summary>沒有參數時範本原樣回（同舊的 <c>MessageText.Get</c>）。</summary>
    internal static string Format(string template, object[] args) =>
        args.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args);
}
