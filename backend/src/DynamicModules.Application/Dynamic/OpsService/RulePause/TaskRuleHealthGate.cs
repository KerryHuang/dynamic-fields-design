using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.OpsService.RulePause;

/// <summary>
/// 任務規則評估時的健康狀態把關（維運設計 §17.2.7「計數」「順序與 reset 協定」），<c>TaskRuleStage</c> 與手動套用共用，都在業務交易內：
/// 已暫停的略過並加 <c>SkippedWhilePaused</c>；每條規則評估前取一次評估時間（UTC、截到微秒）；失敗時在例外篩選裡（回滾之前）
/// 標記評估時間並登記在途失敗；成功時只有「有連續失敗」或「本實例有更早的在途失敗」才寫 reset，其他成功不碰資料庫。
/// </summary>
public sealed class TaskRuleHealthGate(
    IRuleHealthRepository health, RuleFailureInFlight inFlight, ITaskRuleFaultInjector faults, TimeProvider clock, IUnitOfWork uow,
    IDatabaseErrors errors)
{
    private const string Kind = RuleFailureTag.TaskRule;

    public async Task<Dictionary<long, RuleHealth>> LoadAsync(IReadOnlyCollection<long> ruleIds, CancellationToken ct) =>
        (await health.ListAsync(Kind, ruleIds, ct)).ToDictionary(h => h.RuleId);

    public static bool IsPaused(IReadOnlyDictionary<long, RuleHealth> rows, long ruleId) =>
        rows.TryGetValue(ruleId, out var row) && row.PausedUtc is not null;

    /// <summary>評估時間：UTC（不是 IBusinessTime 的牆上時間），截到資料庫精度，之後要和讀回的 <c>StreakResetUtc</c> 比先後。</summary>
    public DateTime Now() => StorageTime.ToMicroseconds(clock.GetUtcNow().UtcDateTime);

    public void Inject(TaskRule rule) => faults.Inject(rule);

    /// <summary>給例外篩選用（永遠回 false，不吞例外）：標記規則與評估時間、登記在途失敗。</summary>
    public bool OnFailure(Exception e, long ruleId, DateTime evaluatedUtc) =>
        RuleFailureTag.Mark(e, Kind, ruleId, evaluatedUtc) | inFlight.Register(ruleId, evaluatedUtc);

    public Task SkipAsync(long ruleId, CancellationToken ct) => health.AddSkippedAsync(Kind, ruleId, 1, ct);

    /// <summary>
    /// 成功的 reset：(a) 有健康列且連續失敗大於 0；或 (b) 本實例有這條規則、評估時間更早的在途失敗。還沒有列而符合 (b) 時新增一列
    /// （計數 0、<c>StreakResetUtc</c>＝評估時間），撞主鍵改走更新。呼叫端要在規則的例外篩選之外呼叫（這裡的資料庫錯誤不是規則失敗）。
    /// </summary>
    public async Task OnSuccessAsync(IDictionary<long, RuleHealth> rows, long ruleId, DateTime evaluatedUtc, CancellationToken ct)
    {
        rows.TryGetValue(ruleId, out var row);
        var streak = row is { ConsecutiveFailures: > 0 };
        if (!streak && !inFlight.HasEarlier(ruleId, evaluatedUtc)) return;
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
