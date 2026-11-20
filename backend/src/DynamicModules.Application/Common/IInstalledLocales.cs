using DynamicModules.Domain.Policies;

namespace DynamicModules.Application.Common;

/// <summary>
/// 系統已安裝的語系代碼（語系類政策的選項；政策設計 p3 計畫判斷 22、語系包設計 §4.3）。
/// 出廠實作是程式支援的四種語系；i4a 語系包換成實際安裝的清單，呼叫端不用改。
/// </summary>
public interface IInstalledLocales
{
    IReadOnlyList<string> Codes { get; }
}

public sealed class FactoryInstalledLocales : IInstalledLocales
{
    public IReadOnlyList<string> Codes => PolicyCatalog.FactoryLocales;
}
