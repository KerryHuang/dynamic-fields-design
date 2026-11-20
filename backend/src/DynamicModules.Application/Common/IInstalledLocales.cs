namespace DynamicModules.Application.Common;

/// <summary>
/// 系統已安裝的語系代碼（語系類政策的選項；政策設計 p3 計畫判斷 22、語系包設計 §4.3）。實作是 <see cref="LocaleRegistry"/>。
/// </summary>
public interface IInstalledLocales
{
    IReadOnlyList<string> Codes { get; }
}
