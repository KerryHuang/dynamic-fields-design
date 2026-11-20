using DynamicModules.Application.Dynamic.PolicyService;

namespace DynamicModules.Application.Common;

/// <summary>這個請求的介面語系（WebAPI 讀 X-Locale 標頭）；一定是啟用中的語系，缺值、不認得或已停用時用預設語系（政策 locale.default）。</summary>
public interface ICurrentLocale
{
    string Locale { get; }
}

/// <summary>沒有請求可讀的環境（背景流程、Application 測試）用的預設：系統預設語系（政策 locale.default，出廠繁中）。</summary>
public sealed class FallbackCurrentLocale(CachedSystemPolicy cache) : ICurrentLocale
{
    public string Locale => cache.DefaultLocale;
}
