using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.OpsService.RulePause;

/// <summary>
/// 任務規則評估時的健康狀態把關（維運設計 §17.2.7「計數」「順序與 reset 協定」），<c>TaskRuleStage</c> 與手動套用共用，都在業務交易內：
/// 已暫停的略過並加 <c>SkippedWhilePaused</c>；每條規則評估前呼叫 <see cref="BeginAsync"/> 取評估時間（UTC、截到微秒；有連續失敗時先鎖規則列）；
/// 失敗時在例外篩選裡（回滾之前）標記評估時間並登記在途失敗；成功時只有「有連續失敗」或「本實例有更早的在途失敗」才寫 reset，其他成功不碰資料庫。
/// </summary>
public sealed class TaskRuleHealthGate(
    IRuleHealthRepository health, ITaskRuleRepository rules, RuleFailureInFlight inFlight, ITaskRuleFaultInjector faults, TimeProvider clock,
    IUnitOfWork uow, IDatabaseErrors errors)
{
    private const string Kind = RuleFailureTag.TaskRule;

    /// <summary>每條規則讀健康列之前的在途完成序號（<see cref="RuleFailureInFlight.Snapshot"/>）；沒讀過的當 0（最保守）。</summary>
    private readonly Dictionary<long, long> _loadedAt = [];

    public async Task<Dictionary<long, RuleHealth>> LoadAsync(IReadOnlyCollection<long> ruleIds, CancellationToken ct)
    {
        // 先取快照再讀：快照之後才補記完的失敗可能不在讀到的列裡，成功時仍當成在途（§17.14 審查修正 1）
        var seq = inFlight.Snapshot();
        foreach (var id in ruleIds) _loadedAt[id] = seq;
        return (await health.ListAsync(Kind, ruleIds, ct)).ToDictionary(h => h.RuleId);
    }

    public static bool IsPaused(IReadOnlyDictionary<long, RuleHealth> rows, long ruleId) =>
        rows.TryGetValue(ruleId, out var row) && row.PausedUtc is not null;

    /// <summary>評估時間：UTC（不是 IBusinessTime 的牆上時間），截到資料庫精度，之後要和讀回的 <c>StreakResetUtc</c> 比先後。</summary>
    public DateTime Now() => StorageTime.ToMicroseconds(clock.GetUtcNow().UtcDateTime);

    /// <summary>
    /// 評估一條規則之前呼叫，回傳評估時間；這條規則在讀列之後已被暫停時回 null（呼叫端當成暫停略過）。
    /// 讀到的列有連續失敗、或本實例有更早的在途失敗（這次成功多半要寫 reset）時，先鎖規則列（與失敗補記第一句同一把，
    /// <see cref="ITaskRuleRepository.LockAsync"/>）到業務交易結束、重讀健康列，鎖到之後才取評估時間：
    /// 評估較晚的失敗，其業務交易或補記排在這次成功提交（或回滾）之後，看得到它的 reset，不會在看不到成功的情況下數到門檻而暫停
    /// （§17.14 審查修正第二輪 1）。等到的是較早的補記時，評估時間晚於它、它先算。其他成功不鎖，成功的路徑仍然通常零寫入。
    /// </summary>
    public async Task<DateTime?> BeginAsync(IDictionary<long, RuleHealth> rows, long ruleId, CancellationToken ct)
    {
        var now = Now();
        rows.TryGetValue(ruleId, out var row);
        if (row is not { ConsecutiveFailures: > 0 } && !inFlight.HasEarlier(ruleId, now, _loadedAt.GetValueOrDefault(ruleId))) return now;
        await rules.LockAsync([ruleId], ct);
        // 鎖之後重讀：等鎖期間提交的補記（含暫停）要看得到；之後的補記排在這個交易之後，快照不再過時
        _loadedAt[ruleId] = inFlight.Snapshot();
        var fresh = await health.GetAsync(Kind, ruleId, ct);
        if (fresh is null) rows.Remove(ruleId);
        else rows[ruleId] = fresh;
        return fresh?.PausedUtc is null ? Now() : null;
    }

    public void Inject(TaskRule rule) => faults.Inject(rule);

    /// <summary>給例外篩選用（永遠回 false，不吞例外）：標記規則與評估時間、登記在途失敗。</summary>
    public bool OnFailure(Exception e, long ruleId, DateTime evaluatedUtc) =>
        RuleFailureTag.Mark(e, Kind, ruleId, evaluatedUtc) | inFlight.Register(ruleId, evaluatedUtc);

    public Task SkipAsync(long ruleId, CancellationToken ct) => health.AddSkippedAsync(Kind, ruleId, 1, ct);

    /// <summary>
    /// 成功的 reset：(a) 讀到的健康列連續失敗大於 0；或 (b) 本實例有這條規則、評估時間更早，而且還沒補記完、或在讀健康列之後才補記完的失敗
    /// （讀到的列看不到它）。還沒有列而符合 (b) 時新增一列（計數 0、<c>StreakResetUtc</c>＝評估時間），撞主鍵改走更新。
    /// 呼叫端要在規則的例外篩選之外呼叫（這裡的資料庫錯誤不是規則失敗）。
    /// </summary>
    public async Task OnSuccessAsync(IDictionary<long, RuleHealth> rows, long ruleId, DateTime evaluatedUtc, CancellationToken ct)
    {
        rows.TryGetValue(ruleId, out var row);
        var streak = row is { ConsecutiveFailures: > 0 };
        if (!streak && !inFlight.HasEarlier(ruleId, evaluatedUtc, _loadedAt.GetValueOrDefault(ruleId))) return;
        if (row is not null || await health.ResetStreakAsync(Kind, ruleId, evaluatedUtc, ct) > 0)
        {
            if (row is not null) await health.ResetStreakAsync(Kind, ruleId, evaluatedUtc, ct);
            // 同一請求後面的記錄再評估這條規則時不必再寫一次
            rows[ruleId] = new RuleHealth { RuleKind = Kind, RuleId = ruleId, StreakResetUtc = evaluatedUtc, PausedUtc = row?.PausedUtc };
            return;
        }
        var created = new RuleHealth { RuleKind = Kind, RuleId = ruleId, StreakResetUtc = evaluatedUtc };
        health.Add(created);
        try
        {
            await uow.CommitAsync(ct);
        }
        catch (Exception e) when (errors.IsUniqueViolation(e))
        {
            // 別的請求剛新增（例如那筆在途失敗的補記）：拿掉這列、改走只往後推的更新
            health.Detach(created);
            await health.ResetStreakAsync(Kind, ruleId, evaluatedUtc, ct);
        }
        rows[ruleId] = new RuleHealth { RuleKind = Kind, RuleId = ruleId, StreakResetUtc = evaluatedUtc };
    }
}
