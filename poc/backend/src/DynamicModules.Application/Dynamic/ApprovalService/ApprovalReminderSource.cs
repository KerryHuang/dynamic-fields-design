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
    IApprovalInstanceRepository repo, IModuleRepository modules, ApprovalPolicies policies, ApprovalNotifier notifier, IBusinessTime time)
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
        return (await DueAsync(userId, now, track: false, ct)).Count > 0;
    }

    public async Task AddDueAsync(string userId, DateOnly today, CancellationToken ct)
    {
        var now = time.Now;
        var notified = new HashSet<long>();
        foreach (var (task, inst) in await DueAsync(userId, now, track: true, ct))
        {
            if (notified.Add(inst.Id)) await notifier.NotifyAsync(NotificationKinds.ApprovalOverdue, inst, [userId], exceptUserId: null, ct);
            task.LastRemindedTime = now;
        }
    }

    /// <summary>逐筆以任務所在模組的生效間隔與模組狀態確認；資料庫端先以最短可能的間隔（政策下限 1 小時）擋掉剛提醒過的。</summary>
    private async Task<List<(ApprovalTask Task, ApprovalInstance Instance)>> DueAsync(string userId, DateTime now, bool track, CancellationToken ct)
    {
        var tasks = await repo.ListOverdueOpenAsync(userId, now, now.AddHours(-PolicyCatalog.ApprovalOverdueReminderHours.Min), track, ct);
        var instances = (await repo.ListInstancesAsync([.. tasks.Select(t => t.InstanceId).Distinct()], ct)).ToDictionary(i => i.Id);
        var result = new List<(ApprovalTask, ApprovalInstance)>();
        foreach (var task in tasks)
        {
            if (!instances.TryGetValue(task.InstanceId, out var inst)) continue;
            var interval = await policies.OverdueReminderHoursAsync(inst.ModuleId, ct);
            if (task.LastRemindedTime is { } last && last >= now.AddHours(-interval)) continue;
            if (!(await modules.GetByIdAsync(inst.ModuleId, includeChildren: false, ct)).IsWritable()) continue;
            result.Add((task, inst));
        }
        return result;
    }
}
