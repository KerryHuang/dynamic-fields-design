using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Common;

/// <summary>
/// 必填的多語文字依政策 locale.requiredText 檢查（政策設計 §3.5、p3 計畫判斷 9、11）。
/// 新增時 <c>stored</c> 傳 null；修改時傳資料庫裡的值，內容沒變（<see cref="I18nText.SameAs"/>）就不檢查新增的必填語系，
/// 政策調嚴不溯及既有資料。開啟應用程式套件與樣板期間只要求繁中（<see cref="PackageTextScope"/>，判斷 12）。
/// </summary>
public sealed class RequiredTextGuard(CachedSystemPolicy cache)
{
    /// <summary>不合格回 <c>{subject}必須有{語系名稱}</c>，否則 null。出廠值時與政策化前的訊息逐字相同（例：名稱必須有繁體中文）。</summary>
    public string? Check(I18nText? incoming, I18nText? stored, string subject)
    {
        var required = RequiredLocales;
        if (incoming is null || !incoming.IsWellFormed(out _)) return Message(subject, required);
        if (stored is not null && incoming.SameAs(stored)) return null;
        return incoming.MissingLocales(required) is { Count: > 0 } missing ? Message(subject, missing) : null;
    }

    /// <summary>只檢查格式（修改類的 FluentValidation 用；必填語系在 handler 載入舊值後以 <see cref="Check"/> 檢查）。</summary>
    public string? CheckWellFormed(I18nText? incoming, string subject) =>
        incoming is null || !incoming.IsWellFormed(out _) ? Message(subject, RequiredLocales) : null;

    /// <summary>目前要求的必填語系：開啟套件或樣板期間是繁中（<see cref="PackageTextScope"/>），其餘照政策。</summary>
    public IReadOnlyList<string> RequiredLocales => PackageTextScope.Active ? PackageTextScope.RequiredLocales : cache.RequiredTextLocales;

    public static string Message(string subject, IEnumerable<string> locales) => $"{subject}必須有{LocaleNames.Join(locales)}";
}

/// <summary>語系代碼的中文名稱（訊息用）；不在表內的代碼原樣輸出（i4a 的自訂語系）。</summary>
public static class LocaleNames
{
    public static readonly IReadOnlyDictionary<string, string> Zh = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["zh-TW"] = "繁體中文", ["en-US"] = "英文", ["th"] = "泰文", ["vi"] = "越南文",
    };

    public static string Join(IEnumerable<string> locales) => string.Join("、", locales.Select(l => Zh.GetValueOrDefault(l, l)));
}
