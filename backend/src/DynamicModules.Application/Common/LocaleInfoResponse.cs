using System.Text.Json.Serialization;

namespace DynamicModules.Application.Common;

/// <summary>
/// 一個語系的資訊（<c>auth/options</c>、<c>policies/effective</c> 的 <c>locales</c>；語系包設計 §4.4，i4a 計畫判斷 12、13）。
/// 全域 JSON 設定略過 null，<c>QuasarLang</c>、<c>FallbackCode</c> 要永遠帶出（值可為 null）。<c>uiHash</c> 在 i4b 再加。
/// </summary>
public sealed record LocaleInfoResponse(
    string Code, string NativeName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? QuasarLang,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FallbackCode)
{
    public static LocaleInfoResponse Of(InstalledLocaleInfo l) => new(l.Code, l.NativeName, l.QuasarLang, l.FallbackCode);
}
