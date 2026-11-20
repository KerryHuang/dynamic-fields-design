using DynamicModules.Domain.Policies;

namespace DynamicModules.Application.Dynamic.PolicyService;

public static class PolicyMessages
{
    public const string NotFound = "找不到政策";
    public const string ModuleNotFound = "找不到模組";
    public const string SystemOnly = "這個政策只能在系統設定改，不能由模組覆寫";
    public const string NotForThisModule = "這個政策不適用於這種模組";

    /// <summary>白名單含寫死黑名單項目（網頁、腳本、可執行類）時，列出被拒的項目。</summary>
    public static string Forbidden(IEnumerable<string> rejected) =>
        $"不能收這些類型（網頁、腳本或可執行檔，基於安全不開放）：{string.Join("、", rejected)}";

    /// <summary>收件人政策引用了不存在或已停用的群組、使用者。</summary>
    public static string RecipientsMissing(IEnumerable<string> items) =>
        $"這些收件人找不到或已停用：{string.Join("、", items)}";

    /// <summary>刪群組被收件人政策擋下。</summary>
    public static string GroupUsedByPolicies(IEnumerable<string> codes) =>
        $"群組被政策設定使用：{string.Join("、", codes)}";

    /// <summary>改營運時區會讓日期時間索引平移出可儲存的範圍（min、max 是超界那幾筆的索引值）。</summary>
    public static string TimeZoneShiftOutOfRange(int count, DateTime min, DateTime max) =>
        $"改成這個時區會讓 {count} 筆日期時間超出可儲存的範圍（{min.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} ～ {max.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)}），請先修改這些記錄再改時區";

    /// <summary>存檔被擋時寫出允許範圍；<paramref name="options"/> 來自 <see cref="PolicyOptionSource"/>。語系類的 RoleSet 寫「語系」（計畫判斷 21）。</summary>
    public static string Invalid(PolicyDefinition d, IReadOnlyList<string>? options) => d.Kind switch
    {
        PolicyKind.Integer => $"必須是 {d.Min} 到 {d.Max} 的整數",
        PolicyKind.Boolean => "必須是 true 或 false",
        PolicyKind.Choice => $"必須是以下其中一個：{string.Join("、", options ?? [])}",
        PolicyKind.RoleSet when PolicyCatalog.IsLocaleOptions(d) => LocaleNotInstalled(options ?? []),
        PolicyKind.RoleSet => $"只能從以下角色挑選：{string.Join("、", options ?? [])}",
        PolicyKind.CodeList => $"要有 {d.Min} 到 {d.Max} 個，每個只能是 1 到 {PolicyDefinition.CodeItemMaxLength} 個小寫英文或數字",
        PolicyKind.Recipients => $"要有 {d.Min} 到 {d.Max} 項，每項是 admins、group:群組編號或 user:帳號",
        PolicyKind.UtcOffset => "必須是 UTC 偏移，例如 +08:00",
        _ => "值不正確",
    };

    public static string LocaleNotInstalled(IEnumerable<string> installed) => $"只能從以下語系挑選：{string.Join("、", installed)}";
    public const string LocaleEnabledEmpty = "至少要啟用一種語系";
    public const string LocaleDefaultNotEnabled = "預設語系必須是啟用中的語系；要停用它，請先改預設語系";
    public const string LocaleRequiredEmpty = "至少要有一種必填語系";
    public const string LocaleRequiredNotEnabled = "必填語系必須都是啟用中的語系；要停用它，請先從必填語系拿掉";

    /// <summary><see cref="LocalePolicyRules.Check"/> 的錯誤代碼轉成訊息。</summary>
    public static string LocaleRule(string code, IEnumerable<string> installed) => code switch
    {
        LocalePolicyRules.NotInstalled => LocaleNotInstalled(installed),
        LocalePolicyRules.EnabledEmpty => LocaleEnabledEmpty,
        LocalePolicyRules.DefaultNotEnabled => LocaleDefaultNotEnabled,
        LocalePolicyRules.RequiredEmpty => LocaleRequiredEmpty,
        LocalePolicyRules.RequiredNotEnabled => LocaleRequiredNotEnabled,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };
}
