namespace DynamicModules.Application.Dynamic.LocaleService.Packs;

/// <summary>
/// 語系包匯入的部署開關（語系包設計 §13.4；i4d1 計畫判斷 15）：設定 <c>Localization:PackImportEnabled</c>，沒設時 <c>Demo:LoginEnabled</c> 為 true 就關閉。
/// 關閉時預覽照常產生報告但不暫存（<c>previewId</c> 為 null），套用、回到上一版、移除覆寫、移除語系回 403。WebAPI 依設定註冊；
/// 沒有註冊時（自己組 DI 的測試）是開放。
/// </summary>
public sealed record LocalePackSettings(bool ImportEnabled);
