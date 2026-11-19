using System.Collections.Concurrent;

namespace DynamicModules.Application.Dynamic.OpsService.RulePause;

/// <summary>
/// 本實例「已失敗、還沒補記」的任務規則（維運設計 §17.2.7 在途失敗、O2-T30）：<c>TaskRuleStage</c> 的例外篩選（業務交易回滾之前）登記，
/// <c>RuleFailureBehavior</c> 補記完成或判定不歸屬時移除。只在記憶體；沒被移除的過 <see cref="Expiry"/> 視為不存在（技術限制，§17.9）。
/// 同一條規則可能同時有多筆在途失敗（並行請求），各自以評估時間區分。
/// </summary>
public sealed class RuleFailureInFlight(TimeProvider time)
{
    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(long RuleId, DateTime EvaluatedUtc), DateTimeOffset> _entries = new();

    /// <summary>給例外篩選用：登記後回 false（不吞例外）。</summary>
    public bool Register(long ruleId, DateTime evaluatedUtc)
    {
        _entries[(ruleId, evaluatedUtc)] = time.GetUtcNow();
        return false;
    }

    public void Remove(long ruleId, DateTime evaluatedUtc) => _entries.TryRemove((ruleId, evaluatedUtc), out _);

    /// <summary>這條規則有沒有評估時間早於 <paramref name="beforeUtc"/>、還沒過期的在途失敗。</summary>
    public bool HasEarlier(long ruleId, DateTime beforeUtc)
    {
        var cutoff = time.GetUtcNow() - Expiry;
        foreach (var (key, at) in _entries)
        {
            if (at < cutoff) { _entries.TryRemove(key, out _); continue; }
            if (key.RuleId == ruleId && key.EvaluatedUtc < beforeUtc) return true;
        }
        return false;
    }
}
