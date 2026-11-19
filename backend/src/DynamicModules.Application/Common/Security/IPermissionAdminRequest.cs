namespace DynamicModules.Application.Common.Security;

/// <summary>
/// 送出的人必須有「權限設定」（SYS00001，權限軸設計 §7.1）；由 PermissionAdminBehavior 把關，
/// 通過後整個請求（含交易與存檔）在 Authorized 寫入許可內執行。寫授權資料的請求一律宣告，沒有例外（T7）。
/// </summary>
public interface IPermissionAdminRequest { }
