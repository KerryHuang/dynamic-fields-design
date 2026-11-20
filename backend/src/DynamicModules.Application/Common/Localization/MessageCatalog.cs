using System.Globalization;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DynamicModules.Application.Common.Localization;

/// <summary>
/// 後端訊息目錄（singleton；語系包設計 §9.1，i4c 計畫判斷 1、2）。依賴的 <see cref="LocaleRegistry"/>（已安裝清單與上層關係）
/// 與 <see cref="CachedSystemPolicy"/>（預設語系）都是記憶體快照，查一句不碰資料庫。鏈上每個語系先查覆寫層（語系包匯入的，
/// <see cref="LocaleTexts.ServerOverride"/>）、再查內建層（<see cref="BuiltinServerText"/>，只認內建四語）（i4d1 計畫判斷 11）。
/// 覆寫範本格式化失敗（平台升級改了內建範本的參數個數）時改用同語系的內建層、沒有就往鏈下一個，記 Warning，不讓 API 變 500。
/// </summary>
public sealed class MessageCatalog(LocaleRegistry registry, CachedSystemPolicy policy, ILogger<MessageCatalog>? logger = null) : IMessageCatalog
{
    private readonly ILogger<MessageCatalog> _logger = logger ?? NullLogger<MessageCatalog>.Instance;

    /// <summary>只給測試：<see cref="AsI18n"/> 擷取快照之後、產生每個語系之前呼叫（參數是那個語系）。</summary>
    public Action<string>? BeforeLocaleForTests { get; set; }

    public string Get(LocaleContext lc, string key, params object[] args) => Get(registry.Texts(), lc, key, args);

    /// <summary>預設語系只讀一次、已安裝語系、每個語系的鏈與覆寫層取自同一份 registry 快照（判斷 2）；已停用的語系也產生（M3）。</summary>
    public I18nText AsI18n(string key, params object[] args)
    {
        var texts = registry.Texts();
        var contexts = texts.ContextsFor(policy.DefaultLocale);
        var values = new List<KeyValuePair<string, string>>(contexts.Count);
        foreach (var lc in contexts)
        {
            BeforeLocaleForTests?.Invoke(lc.Locale);
            values.Add(KeyValuePair.Create(lc.Locale, Get(texts, lc, key, args)));
        }
        return new I18nText(values);
    }

    private string Get(LocaleTexts texts, LocaleContext lc, string key, object[] args)
    {
        foreach (var locale in Chain(lc))
        {
            if (texts.ServerOverride(locale, key) is { Length: > 0 } overlay)
            {
                try { return Format(overlay, args); }
                catch (FormatException e) { _logger.LogWarning(e, "語系 {Locale} 的覆寫訊息 {Key} 無法格式化，改用內建層", locale, key); }
            }
            if (BuiltinServerText.TryGet(locale, key, out var builtin) && builtin.Length > 0) return Format(builtin, args);
        }
        throw new KeyNotFoundException($"後端訊息沒有這個鍵：{key}");
    }

    /// <summary>沿鏈取第一個有值（非 null、非空字串）的層；全部缺值回 null（判斷 12：呼叫端對應成空字串或例外）。</summary>
    public static string? FirstInChain(IEnumerable<string> chain, Func<string, string?> layer)
    {
        foreach (var locale in chain)
            if (layer(locale) is { Length: > 0 } text) return text;
        return null;
    }

    /// <summary>[lc.Locale, ..lc.Fallbacks, zh-TW] 去重。</summary>
    private static IEnumerable<string> Chain(LocaleContext lc) =>
        lc.Fallbacks.Prepend(lc.Locale).Append(I18nText.FallbackLocale).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// 沿鏈找範本，每個語系先試 <paramref name="overlay"/>（語系、鍵 → 覆寫範本；null＝只看內建層）再試內建範本；繁中內建層完整，找不到就是鍵寫錯。
    /// 不處理格式化失敗的退回（那是 <see cref="Get(LocaleContext, string, object[])"/> 的事）。
    /// </summary>
    internal static string Lookup(LocaleContext lc, string key, Func<string, string, string?>? overlay) =>
        FirstInChain(Chain(lc), l => overlay?.Invoke(l, key) is { Length: > 0 } o ? o : BuiltinServerText.TryGet(l, key, out var t) ? t : null)
        ?? throw new KeyNotFoundException($"後端訊息沒有這個鍵：{key}");

    /// <summary>沒有參數時範本原樣回（同舊的 <c>MessageText.Get</c>）。</summary>
    internal static string Format(string template, object[] args) =>
        args.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args);
}
