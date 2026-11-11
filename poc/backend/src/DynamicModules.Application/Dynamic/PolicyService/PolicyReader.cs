using System.Globalization;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.PolicyService;

/// <summary>一個政策在各層的值（都是標準字串）：出廠、全系統、模組覆寫（沒設或無法使用為 null）與生效值。</summary>
public sealed record PolicyResolution(string Factory, string? System, string? Override, string Effective);

/// <summary>
/// 政策的唯一讀值入口（設計 docs/superpowers/specs/2026-11-11-policy-settings-design.md §3.1、§6）。
/// 查值順序：模組覆寫 → 全系統值 → 部署出廠值 → 程式出廠值；壞值退回上一層並記 Warning。同一個 scope 內快取。
/// </summary>
public interface IPolicyReader
{
    Task<PolicyResolution> ResolveAsync(PolicyDefinition def, long? moduleId, CancellationToken ct);
    Task<int> IntAsync(PolicyDefinition def, long? moduleId, CancellationToken ct);
    Task<bool> BoolAsync(PolicyDefinition def, long? moduleId, CancellationToken ct);
    Task<string> ChoiceAsync(PolicyDefinition def, long? moduleId, CancellationToken ct);
    /// <summary>RoleSet 與 CodeList；空字串回空清單。</summary>
    Task<IReadOnlyList<string>> ListAsync(PolicyDefinition def, long? moduleId, CancellationToken ct);
    /// <summary>寫入政策後呼叫，清掉這個 scope 的快取。</summary>
    void Invalidate();
}

public sealed class PolicyReader(
    ISystemSettingRepository settings, IModulePolicyRepository overrides, PolicyEnvironmentDefaults env, ILogger<PolicyReader> logger)
    : IPolicyReader
{
    private const string SystemPrefix = "policy.";

    private IReadOnlyDictionary<string, string>? _system;
    private readonly Dictionary<long, Dictionary<string, string>> _overrides = [];

    public async Task<PolicyResolution> ResolveAsync(PolicyDefinition def, long? moduleId, CancellationToken ct)
    {
        var factory = env.FactoryOf(def);
        _system ??= await settings.ListByPrefixAsync(SystemPrefix, ct);
        var system = Usable(def, _system.GetValueOrDefault(def.SettingKey), "全系統");

        string? overrideValue = null;
        if (def.Scope == PolicyScope.Module && moduleId is { } id)
        {
            if (!_overrides.TryGetValue(id, out var rows))
            {
                rows = (await overrides.ListByModuleAsync(id, ct)).ToDictionary(p => p.Code, p => p.Value, StringComparer.Ordinal);
                _overrides[id] = rows;
            }
            overrideValue = Usable(def, rows.GetValueOrDefault(def.Code), "模組覆寫");
        }

        return new PolicyResolution(factory, system, overrideValue, overrideValue ?? system ?? factory);
    }

    public async Task<int> IntAsync(PolicyDefinition def, long? moduleId, CancellationToken ct)
    {
        Expect(def, PolicyKind.Integer);
        return int.Parse((await ResolveAsync(def, moduleId, ct)).Effective, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    public async Task<bool> BoolAsync(PolicyDefinition def, long? moduleId, CancellationToken ct)
    {
        Expect(def, PolicyKind.Boolean);
        return (await ResolveAsync(def, moduleId, ct)).Effective == "true";
    }

    public async Task<string> ChoiceAsync(PolicyDefinition def, long? moduleId, CancellationToken ct)
    {
        Expect(def, PolicyKind.Choice);
        return (await ResolveAsync(def, moduleId, ct)).Effective;
    }

    public async Task<IReadOnlyList<string>> ListAsync(PolicyDefinition def, long? moduleId, CancellationToken ct)
    {
        Expect(def, PolicyKind.RoleSet, PolicyKind.CodeList);
        var effective = (await ResolveAsync(def, moduleId, ct)).Effective;
        return effective.Length == 0 ? [] : effective.Split(',');
    }

    public void Invalidate()
    {
        _system = null;
        _overrides.Clear();
    }

    /// <summary>存在但無法使用的值記 Warning 並當成沒設。</summary>
    private string? Usable(PolicyDefinition def, string? stored, string layer)
    {
        if (stored is null) return null;
        var normal = def.Normalize(stored);
        if (normal is null)
            logger.LogWarning("政策 {Code} 的{Layer}值「{Value}」無法使用，改用上一層", def.Code, layer, stored);
        return normal;
    }

    private static void Expect(PolicyDefinition def, params PolicyKind[] kinds)
    {
        if (!kinds.Contains(def.Kind))
            throw new InvalidOperationException($"政策 {def.Code} 的型別是 {def.Kind}，不能用 {string.Join("／", kinds)} 讀取");
    }
}
