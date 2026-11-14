using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.NotificationService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 簽核逾期的提醒（設計 §12、計畫 a3e U6）：只提醒任務的負責人（代理任務只給代理人），任務 open、DueTime 已過、實例 pending、模組可寫，
/// 上次提醒（LastRemindedTime）為空或已超過該模組的間隔（政策 approval.overdueReminderHours）才補一則 approvalOverdue。
/// 以 <see cref="IBusinessTime.Now"/> 比對；<paramref name="today"/> 不用，因為間隔以小時計。
/// </summary>
public sealed class ApprovalReminderSource(
    IApprovalInstanceRepository repo, ApprovalPolicies policies, ApprovalNotifier notifier, IBusinessTime time)
    : INotificationReminderSource
{
    public async Task<bool> AnyDueAsync(string userId, DateOnly today, CancellationToken ct)
    {
        var now = time.Now;
        var moduleIds = await repo.ListOverdueOpenModuleIdsAsync(userId, now, ct);
        if (moduleIds.Count == 0) return false;
        // 先以所有逾期任務所在模組的最短間隔做資料庫端存在查詢；成立才逐筆以各模組的間隔確認
        var shortest = int.MaxValue;
        foreach (var id in moduleIds) shortest = Math.Min(shortest, await policies.OverdueReminderHoursAsync(id, ct));
        if (!await repo.AnyOverdueOpenAsync(userId, now, now.AddHours(-shortest), ct)) return false;
        return (await DueAsync(userId, now, ct)).Count > 0;
    }

    public async Task AddDueAsync(string userId, DateOnly today, CancellationToken ct)
    {
        var now = time.Now;
        var notified = new HashSet<long>();
        foreach (var (task, inst, interval) in await DueAsync(userId, now, ct))
        {
            // 條件式認領：別的請求已先補過就不再寫通知（影響列數 1 才算數）
            if (!await repo.TryClaimReminderAsync(task.Id, now, now.AddHours(-interval), ct)) continue;
            if (notified.Add(inst.Id)) await notifier.NotifyAsync(NotificationKinds.ApprovalOverdue, inst, [userId], exceptUserId: null, ct);
        }
    }

    /// <summary>
    /// 候選任務（資料庫端已篩任務、實例與模組可寫）再逐筆以所在模組的生效間隔確認；實例一次載入、各模組的間隔經政策讀取器快取，不逐任務查。
    /// 資料庫端先以最短可能的間隔（政策下限）擋掉剛提醒過的。
    /// </summary>
    private async Task<List<(ApprovalTask Task, ApprovalInstance Instance, int Interval)>> DueAsync(string userId, DateTime now, CancellationToken ct)
    {
        var tasks = await repo.ListOverdueOpenAsync(userId, now, now.AddHours(-PolicyCatalog.ApprovalOverdueReminderHours.Min), ct);
        var instances = (await repo.ListInstancesAsync([.. tasks.Select(t => t.InstanceId).Distinct()], ct)).ToDictionary(i => i.Id);
        var result = new List<(ApprovalTask, ApprovalInstance, int)>();
        foreach (var task in tasks)
        {
            if (!instances.TryGetValue(task.InstanceId, out var inst)) continue;
            var interval = await policies.OverdueReminderHoursAsync(inst.ModuleId, ct);
            if (task.LastRemindedTime is { } last && last >= now.AddHours(-interval)) continue;
            result.Add((task, inst, interval));
        }
        return result;
    }
}
