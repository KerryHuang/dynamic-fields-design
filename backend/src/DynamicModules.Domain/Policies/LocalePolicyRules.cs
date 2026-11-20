namespace DynamicModules.Domain.Policies;

/// <summary>
/// 語系三政策（預設、啟用、必填語系）的互相約束（政策設計 p3 計畫判斷 10）：至少啟用一種；預設語系必須啟用；
/// 必填語系至少一種且都要啟用；所有值都要是已安裝的語系。回 null 表示合法，否則回錯誤代碼（訊息由 Application 對應）。
/// </summary>
public static class LocalePolicyRules
{
    public const string NotInstalled = "notInstalled";
    public const string EnabledEmpty = "enabledEmpty";
    public const string DefaultNotEnabled = "defaultNotEnabled";
    public const string RequiredEmpty = "requiredEmpty";
    public const string RequiredNotEnabled = "requiredNotEnabled";

    public static string? Check(
        string defaultLocale, IReadOnlyList<string> enabled, IReadOnlyList<string> required, IReadOnlyCollection<string> installed)
    {
        if (!installed.Contains(defaultLocale) || enabled.Any(l => !installed.Contains(l)) || required.Any(l => !installed.Contains(l)))
            return NotInstalled;
        if (enabled.Count == 0) return EnabledEmpty;
        if (!enabled.Contains(defaultLocale)) return DefaultNotEnabled;
        if (required.Count == 0) return RequiredEmpty;
        if (required.Any(l => !enabled.Contains(l))) return RequiredNotEnabled;
        return null;
    }
}
