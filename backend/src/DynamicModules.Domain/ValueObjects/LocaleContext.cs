namespace DynamicModules.Domain.ValueObjects;

/// <summary>
/// 要輸出的語系與它的退回鏈（政策設計 p3 計畫判斷 13）：純值，不含 IO 或政策讀取。由 Application 的 <c>RequestLocale</c> 依請求與系統政策組好，
/// 傳給 <see cref="I18nText.Resolve(LocaleContext)"/>；Domain 的呼叫端（例如記錄標題樣板）也收它。
/// </summary>
public sealed record LocaleContext(string Locale, IReadOnlyList<string> Fallbacks);
