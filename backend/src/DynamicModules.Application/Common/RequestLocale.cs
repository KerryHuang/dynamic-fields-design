using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Common;

/// <summary>
/// 組這個請求要輸出的語系與退回鏈（政策設計 p3 計畫判斷 13、27；使用者 2026-11-20 裁決）。<see cref="For"/> 的三條規則（D4，使用者 2026-11-20 裁決）：
/// <list type="number">
/// <item>查詢參數或 body 沒指定語系（null、空字串、空白）→ 請求語系 <see cref="Current"/>（<c>X-Locale</c> 標頭；標頭也沒有、不認得或停用時是預設語系）。</item>
/// <item>指定的語系啟用中 → 照用。</item>
/// <item>明確指定但已停用或不認得 → 系統預設語系 <see cref="SystemDefault"/>（不看標頭，比照標頭帶停用語系時的處理）。</item>
/// </list>
/// 退回鏈依輸出的語系組：上層語系 → 預設語系 → 繁中 → 全部已安裝語系（<see cref="CachedSystemPolicy.FallbacksFor"/>，語系包 i4a 計畫判斷 6）。寫進資料庫、稽核或 log 的系統文字用 <see cref="SystemDefault"/>（N1）。
/// </summary>
public sealed class RequestLocale(ICurrentLocale current, CachedSystemPolicy cache)
{
    public LocaleContext Current => Of(current.Locale);

    public LocaleContext SystemDefault => Of(cache.DefaultLocale);

    public LocaleContext For(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return Current;
        return cache.EnabledLocales.Contains(requested) ? Of(requested) : SystemDefault;
    }

    private LocaleContext Of(string locale) => new(locale, cache.FallbacksFor(locale));
}
