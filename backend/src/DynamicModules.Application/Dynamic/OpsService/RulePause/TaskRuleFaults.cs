using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.OpsService.RulePause;

/// <summary>
/// 任務規則的故障注入（只給驗收用；維運設計 §17.2.7、O2-T16）：<c>TaskRuleStage</c> 與手動套用評估每條規則時呼叫，
/// 要失敗就丟例外，走跟真的例外一樣的路徑。正式環境是空實作；開發環境由部署設定 <c>Ops:FaultInjection:TaskRuleKeys</c> 指定規則代碼，
/// 自動測試用 DI 換掉。
/// </summary>
public interface ITaskRuleFaultInjector
{
    void Inject(TaskRule rule);
}

public sealed class NoTaskRuleFaults : ITaskRuleFaultInjector
{
    public void Inject(TaskRule rule) { }
}

/// <summary>故障注入丟出的例外（E13 矩陣：歸屬規則、計數）。</summary>
public sealed class InjectedFaultException(string ruleKey) : Exception($"故障注入：任務規則 {ruleKey}");

/// <summary>依規則代碼清單注入（開發環境）。</summary>
public sealed class ConfiguredTaskRuleFaults(IReadOnlyCollection<string> ruleKeys) : ITaskRuleFaultInjector
{
    public IReadOnlyCollection<string> RuleKeys => ruleKeys;

    public void Inject(TaskRule rule)
    {
        if (ruleKeys.Contains(rule.Key)) throw new InjectedFaultException(rule.Key);
    }
}
