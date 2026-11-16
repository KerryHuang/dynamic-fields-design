using DynamicModules.Domain.Policies;

namespace DynamicModules.Application.Dynamic.PolicyService;

public static class PolicyMessages
{
    public const string NotFound = "找不到政策";
    public const string ModuleNotFound = "找不到模組";
    public const string SystemOnly = "這個政策只能在系統設定改，不能由模組覆寫";

    /// <summary>白名單含寫死黑名單項目（網頁、腳本、可執行類）時，列出被拒的項目。</summary>
    public static string Forbidden(IEnumerable<string> rejected) =>
        $"不能收這些類型（網頁、腳本或可執行檔，基於安全不開放）：{string.Join("、", rejected)}";

    /// <summary>收件人政策引用了不存在或已停用的群組、使用者。</summary>
    public static string RecipientsMissing(IEnumerable<string> items) =>
        $"這些收件人找不到或已停用：{string.Join("、", items)}";

    /// <summary>刪群組被收件人政策擋下。</summary>
    public static string GroupUsedByPolicies(IEnumerable<string> codes) =>
        $"群組被政策設定使用：{string.Join("、", codes)}";

    /// <summary>存檔被擋時寫出允許範圍。</summary>
    public static string Invalid(PolicyDefinition d) => d.Kind switch
    {
        PolicyKind.Integer => $"必須是 {d.Min} 到 {d.Max} 的整數",
        PolicyKind.Boolean => "必須是 true 或 false",
        PolicyKind.Choice => $"必須是以下其中一個：{string.Join("、", d.Options ?? [])}",
        PolicyKind.RoleSet => $"只能從以下角色挑選：{string.Join("、", d.Options ?? [])}",
        PolicyKind.CodeList => $"要有 {d.Min} 到 {d.Max} 個，每個只能是 1 到 {PolicyDefinition.CodeItemMaxLength} 個小寫英文或數字",
        PolicyKind.Recipients => $"要有 {d.Min} 到 {d.Max} 項，每項是 admins、group:群組編號或 user:帳號",
        _ => "值不正確",
    };
}
