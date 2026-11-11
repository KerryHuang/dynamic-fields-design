using System.Collections.Concurrent;
using DynamicModules.Domain.Policies;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.PolicyService;

/// <summary>
/// 部署環境的出廠值（例如部署設定檔 <c>Attachments</c> 節），以政策代碼為鍵、值為標準字串；有寫的蓋過 <see cref="PolicyDefinition.Factory"/>。
/// 沒有任何部署出廠值時用 <see cref="Empty"/>。
/// </summary>
public sealed class PolicyEnvironmentDefaults(IReadOnlyDictionary<string, string> values, ILogger<PolicyEnvironmentDefaults>? logger = null)
{
    public static readonly PolicyEnvironmentDefaults Empty = new(new Dictionary<string, string>());

    private readonly ConcurrentDictionary<string, string> _resolved = new(StringComparer.Ordinal);

    /// <summary>部署值經 <see cref="PolicyDefinition.Normalize"/>；不合法（非數字、超出技術上下限等）退回程式出廠值並記一次 Warning。</summary>
    public string FactoryOf(PolicyDefinition def) => _resolved.GetOrAdd(def.Code, _ =>
    {
        if (!values.TryGetValue(def.Code, out var raw)) return def.Factory;
        var normal = def.Normalize(raw);
        if (normal is null)
            logger?.LogWarning("政策 {Code} 的部署出廠值「{Value}」無法使用，改用程式出廠值", def.Code, raw);
        return normal ?? def.Factory;
    });
}
