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

    /// <summary>
    /// 把部署設定檔寫的值組成出廠值：<see cref="PolicyDefinition.Normalize"/> 成功直接用；否則 <see cref="PolicyDefinition.Clamp"/>
    /// （夾到技術範圍、丟掉不合格與黑名單項目）並記 Warning；夾不出來就略過、退回程式出廠值並記 Warning。
    /// </summary>
    public static PolicyEnvironmentDefaults Build(
        IEnumerable<(PolicyDefinition Def, string Raw)> configured, List<string> warnings)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (def, raw) in configured)
        {
            if (def.Normalize(raw) is { } ok) { values[def.Code] = ok; continue; }
            if (def.Clamp(raw) is { } clamped)
            {
                values[def.Code] = clamped;
                warnings.Add($"部署設定 {def.Code} 的值「{raw}」超出範圍，改用 {clamped}");
            }
            else
            {
                warnings.Add($"部署設定 {def.Code} 的值「{raw}」無法使用，改用程式出廠值");
            }
        }
        return new PolicyEnvironmentDefaults(values);
    }

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
