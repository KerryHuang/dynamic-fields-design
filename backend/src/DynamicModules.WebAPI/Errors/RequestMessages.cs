using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.WebAPI.Errors;

/// <summary>
/// 例外處理與 middleware 的使用者訊息（語系包 i4c 計畫判斷 6）：從 <c>RequestServices</c> 取請求語系與 <see cref="IMessageCatalog"/>，沿退回鏈取句子。
/// 取不到（沒有 services、services 已釋放、極早期失敗）就用內建繁中，不往外丟例外：例外處理裡再丟例外會蓋掉原本的錯誤。
/// 兩者都是記憶體快照，資料庫寫不進、連不上時也能出訊息。
/// </summary>
public static class RequestMessages
{
    private static readonly LocaleContext Zh = new(I18nText.FallbackLocale, []);

    public static string Text(HttpContext ctx, string key, params object[] args)
    {
        var (messages, lc) = Resolve(ctx);
        return messages.Get(lc, key, args);
    }

    /// <summary>已組好的 <see cref="MessageRef"/>（例如凍結欄位例外帶的訊息），同一條鏈；字面值不翻譯。</summary>
    public static string Text(HttpContext ctx, MessageRef message)
    {
        var (messages, lc) = Resolve(ctx);
        return message.Resolve(messages, lc);
    }

    private static (IMessageCatalog Messages, LocaleContext Lc) Resolve(HttpContext ctx)
    {
        try
        {
            var services = ctx.RequestServices;
            if (services?.GetService<IMessageCatalog>() is { } messages && services.GetService<RequestLocale>() is { } locale)
                return (messages, locale.Current);
        }
        catch (Exception)
        {
            // 落到下面的內建繁中
        }
        return (BuiltinMessageCatalog.Instance, Zh);
    }
}
