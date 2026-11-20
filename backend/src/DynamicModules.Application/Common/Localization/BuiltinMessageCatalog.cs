using DynamicModules.Domain.Localization;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Common.Localization;

/// <summary>
/// 只看內建層的 catalog（i4c 計畫判斷 4）：已安裝清單固定為內建四語，鏈只用傳入的 <see cref="LocaleContext"/>。
/// 只給沒有 DI 的退路：上下文 record 的 catalog 為 null、<see cref="ServiceResult.Message"/>（繁中，給 log 與測試）、沒有 <c>RequestServices</c> 的請求。
/// </summary>
public sealed class BuiltinMessageCatalog : IMessageCatalog
{
    public static BuiltinMessageCatalog Instance { get; } = new();

    private BuiltinMessageCatalog() { }

    public string Get(LocaleContext lc, string key, params object[] args) => MessageCatalog.Format(MessageCatalog.Lookup(lc, key), args);

    public I18nText AsI18n(string key, params object[] args) =>
        new(BuiltinLocales.Codes.Select(l => KeyValuePair.Create(l, Get(new LocaleContext(l, []), key, args))));
}
