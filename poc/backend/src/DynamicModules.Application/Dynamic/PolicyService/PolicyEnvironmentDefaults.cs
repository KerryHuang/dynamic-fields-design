using DynamicModules.Domain.Policies;

namespace DynamicModules.Application.Dynamic.PolicyService;

/// <summary>
/// 部署環境的出廠值（例如部署設定檔 <c>Attachments</c> 節），以政策代碼為鍵、值為標準字串；有寫的蓋過 <see cref="PolicyDefinition.Factory"/>。
/// 沒有任何部署出廠值時用 <see cref="Empty"/>。
/// </summary>
public sealed class PolicyEnvironmentDefaults(IReadOnlyDictionary<string, string> values)
{
    public static readonly PolicyEnvironmentDefaults Empty = new(new Dictionary<string, string>());

    public string FactoryOf(PolicyDefinition def) => values.TryGetValue(def.Code, out var v) ? v : def.Factory;
}
