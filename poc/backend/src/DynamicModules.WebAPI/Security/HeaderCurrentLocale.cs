using DynamicModules.Application.Common;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.WebAPI.Security;

/// <summary>讀 X-Locale 標頭；值不在支援清單、缺值或沒有 HttpContext（背景流程）都是繁中。</summary>
public class HeaderCurrentLocale(IHttpContextAccessor accessor) : ICurrentLocale
{
    public const string HeaderName = "X-Locale";

    public string Locale
    {
        get
        {
            string? value = accessor.HttpContext?.Request.Headers[HeaderName];
            return value is not null && I18nText.SupportedLocales.Contains(value) ? value : I18nText.FallbackLocale;
        }
    }
}
