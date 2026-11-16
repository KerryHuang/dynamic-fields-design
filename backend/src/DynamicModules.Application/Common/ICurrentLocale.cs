using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Common;

/// <summary>這個請求的介面語系（WebAPI 讀 X-Locale 標頭）；一定是 <see cref="I18nText.SupportedLocales"/> 之一，缺值或不認得時是繁中。</summary>
public interface ICurrentLocale
{
    string Locale { get; }
}

/// <summary>沒有請求可讀的環境（背景流程、Application 測試）用的預設：固定繁中。</summary>
public sealed class FallbackCurrentLocale : ICurrentLocale
{
    public string Locale => I18nText.FallbackLocale;
}
