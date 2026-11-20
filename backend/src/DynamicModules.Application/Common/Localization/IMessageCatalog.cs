using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Common.Localization;

/// <summary>
/// 後端訊息（語系包設計 §9；i4c 計畫判斷 1、2）：鍵是語系包 <c>server</c> 段的鍵（<see cref="BuiltinServerText"/>）。
/// 正式路徑一律從 DI 取 <see cref="MessageCatalog"/>；<see cref="BuiltinMessageCatalog"/> 只給沒有 DI 的退路（判斷 4）。
/// </summary>
public interface IMessageCatalog
{
    /// <summary>沿 [lc.Locale, ..lc.Fallbacks, zh-TW] 找第一個有的句子（判斷 1）；鍵不存在丟 <see cref="KeyNotFoundException"/>。</summary>
    string Get(LocaleContext lc, string key, params object[] args);

    /// <summary>對每個已安裝語系各產生一句，整批只讀一份快照（判斷 2）。</summary>
    I18nText AsI18n(string key, params object[] args);
}
