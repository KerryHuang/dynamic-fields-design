using DynamicModules.Application.Common.Observability;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.OpsService.RulePause;

/// <summary>
/// 刪任務規則時（刪單條、刪模組、刪應用程式）同交易刪掉它的健康列與失敗列，暫停事件以 <c>ruleDeleted</c> 結案（維運設計 §17.2.7）。
/// 健康列沒有外鍵（§17.4），不清會留下孤兒與永遠不會結案的暫停事件。恢復時的結案（<c>ruleResumed</c>）也走 <see cref="ResolvePausedAsync"/>。
/// 只依賴事件 repository（刪模組、刪應用程式的路徑不必帶入通知與寄送佇列）。
/// </summary>
public sealed class RulePauseCleanup(IRuleHealthRepository health, IOpsIncidentRepository incidents, ITaskRuleRepository rules, TimeProvider clock)
{
    /// <summary>
    /// 先鎖規則列（與失敗補記同一把，<see cref="ITaskRuleRepository.LockAsync"/>）再刪：補記先拿到鎖時，刪除等它提交後才刪、看得到它寫的列；
    /// 刪除先拿到鎖時，補記等刪除提交後發現規則已不在、什麼都不寫（§17.14 審查修正 2）。呼叫端要在規則列真的刪掉之前、同一交易內呼叫。
    /// </summary>
    public async Task RulesDeletedAsync(IReadOnlyCollection<long> ruleIds, CancellationToken ct)
    {
        if (ruleIds.Count == 0) return;
        await rules.LockAsync(ruleIds, ct);
        await health.DeleteAsync(RuleFailureTag.TaskRule, ruleIds, ct);
        await ResolvePausedAsync(ruleIds, IncidentResolvedReason.RuleDeleted, null, ct);
    }

    /// <summary>暫停事件結案；並行改了狀態就重讀再試，最多三次。</summary>
    public async Task ResolvePausedAsync(IReadOnlyCollection<long> ruleIds, IncidentResolvedReason reason, string? by, CancellationToken ct)
    {
        var at = clock.GetUtcNow().UtcDateTime;
        for (var attempt = 1; attempt <= Incidents.IncidentRecorder.MaxAttempts; attempt++)
        {
            var open = await incidents.ListUnresolvedByRuleAsync(RuleFailureTag.TaskRule, ruleIds, RuleErrorCodes.AutoPaused, ct);
            var missed = 0;
            foreach (var i in open)
                if (!await incidents.TryResolveAsync(i.Id, i.Status, reason, by, at, ct)) missed++;
            if (missed == 0) return;
        }
        throw new InvalidOperationException($"規則暫停事件並行更新重試 {Incidents.IncidentRecorder.MaxAttempts} 次仍落空");
    }
}
