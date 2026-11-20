using DynamicModules.Application.Common;
using DynamicModules.Domain.Policies;

namespace DynamicModules.Application.Dynamic.PolicyService;

/// <summary>
/// 政策選項的唯一出處（計畫判斷 22）：語系類政策（預設、啟用、必填語系）回已安裝語系，其餘回登記的 <see cref="PolicyDefinition.Options"/>。
/// 回應的 options、值的正規化與錯誤訊息都從這裡取。
/// </summary>
public sealed class PolicyOptionSource(IInstalledLocales locales)
{
    public IReadOnlyList<string>? Of(PolicyDefinition def) => PolicyCatalog.IsLocaleOptions(def) ? locales.Codes : def.Options;

    /// <summary><see cref="PolicyDefinition.Normalize"/> 並套用執行期選項。</summary>
    public string? Normalize(PolicyDefinition def, string? raw) => def.Normalize(raw, Of(def));
}
