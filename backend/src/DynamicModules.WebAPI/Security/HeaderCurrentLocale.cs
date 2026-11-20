using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PolicyService;

namespace DynamicModules.WebAPI.Security;

/// <summary>讀 X-Locale 標頭；值不在啟用語系（政策 locale.enabled）、缺值或沒有 HttpContext（背景流程）都用預設語系（政策 locale.default）。</summary>
public class HeaderCurrentLocale(IHttpContextAccessor accessor, CachedSystemPolicy cache) : ICurrentLocale
{
    public const string HeaderName = "X-Locale";

    public string Locale
    {
        get
        {
            string? value = accessor.HttpContext?.Request.Headers[HeaderName];
            return value is not null && cache.EnabledLocales.Contains(value) ? value : cache.DefaultLocale;
        }
    }
}
