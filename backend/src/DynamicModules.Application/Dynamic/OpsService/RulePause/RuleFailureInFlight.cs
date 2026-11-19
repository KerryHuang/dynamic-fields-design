using System.Collections.Concurrent;

namespace DynamicModules.Application.Dynamic.OpsService.RulePause;

/// <summary>
/// 本實例「已失敗、還沒補記」與「剛補記完」的任務規則（維運設計 §17.2.7 在途失敗、O2-T30）：<c>TaskRuleStage</c> 的例外篩選（業務交易回滾之前）登記，
/// <c>RuleFailureBehavior</c> 補記交易結束後標上遞增的完成序號（判定不歸屬、沒有補記的直接移除）。只在記憶體；過 <see cref="Expiry"/> 視為不存在（技術限制，§17.9）。
/// 同一條規則可能同時有多筆在途失敗（並行請求），各自以評估時間區分。
/// <para>
/// 完成序號給成功的 reset 判斷用（§17.14 審查修正 1）：請求讀健康列之前取 <see cref="Snapshot"/>；序號大於快照的補記可能在讀之後才提交、
/// 不在讀到的列裡，仍當成在途；不大於快照的在讀之前已提交，讀到的列看得到。
/// </para>
/// </summary>
public sealed class RuleFailureInFlight(TimeProvider time)
{
    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(5);

    /// <summary>值：登記時間、完成序號（0＝還沒補記完）。</summary>
    private readonly ConcurrentDictionary<(long RuleId, DateTime EvaluatedUtc), (DateTimeOffset At, long DoneSeq)> _entries = new();

    private long _seq;

    /// <summary>給例外篩選用：登記後回 false（不吞例外）。</summary>
    public bool Register(long ruleId, DateTime evaluatedUtc)
    {
        _entries[(ruleId, evaluatedUtc)] = (time.GetUtcNow(), 0);
        return false;
    }

    /// <summary>不歸屬規則本身、沒有補記：直接移除。</summary>
    public void Remove(long ruleId, DateTime evaluatedUtc) => _entries.TryRemove((ruleId, evaluatedUtc), out _);

    /// <summary>補記交易已結束（寫進或寫不進都算）：標上完成序號，留到過期。</summary>
    public void Complete(long ruleId, DateTime evaluatedUtc)
    {
        var seq = Interlocked.Increment(ref _seq);
        _entries.AddOrUpdate((ruleId, evaluatedUtc), _ => (time.GetUtcNow(), seq), (_, v) => (v.At, seq));
    }

    /// <summary>讀健康列之前取：目前最大的完成序號。</summary>
    public long Snapshot() => Interlocked.Read(ref _seq);

    /// <summary>
    /// 這條規則有沒有評估時間早於 <paramref name="beforeUtc"/>、還沒過期，而且還沒補記完、或在快照 <paramref name="since"/> 之後才補記完的失敗。
    /// </summary>
    public bool HasEarlier(long ruleId, DateTime beforeUtc, long since)
    {
        var cutoff = time.GetUtcNow() - Expiry;
        foreach (var (key, v) in _entries)
        {
            if (v.At < cutoff) { _entries.TryRemove(key, out _); continue; }
            if (key.RuleId == ruleId && key.EvaluatedUtc < beforeUtc && (v.DoneSeq == 0 || v.DoneSeq > since)) return true;
        }
        return false;
    }
}
