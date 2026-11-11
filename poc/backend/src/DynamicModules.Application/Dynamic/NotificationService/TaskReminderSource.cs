using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.NotificationService;

/// <summary>
/// 任務的到期提醒（設計 §6.2）：負責人、未完成、到期日正好是今天＋提醒天數（政策 task.reminderLeadDays，taskDueSoon）
/// 或已逾期且該模組開啟逾期提醒（政策 task.remindOverdue，taskOverdue），
/// 記在 LastDueSoonNotified／LastOverdueNotified，同一任務同一種提醒一天最多一則。「今天」是營業日。
/// </summary>
public sealed class TaskReminderSource(ITaskRepository tasks, INotificationRepository notifications, IBusinessTime time, IPolicyReader policies)
    : INotificationReminderSource
{
    public async Task<bool> AnyDueAsync(string userId, DateOnly today, CancellationToken ct)
    {
        var filter = await FilterAsync(userId, today, ct);
        // 先用資料庫端 EXISTS 擋掉絕大多數輪詢；成立時筆數已很小，再逐筆確認（各模組的日期聯集可能多選到別的模組的任務）
        if (!await tasks.AnyReminderDueAsync(userId, today, filter, ct)) return false;
        foreach (var task in await tasks.ListReminderDueAsync(userId, today, filter, track: false, ct))
            if (await DecideAsync(task, today, ct) is (true, _) or (_, true)) return true;
        return false;
    }

    public async Task AddDueAsync(string userId, DateOnly today, CancellationToken ct)
    {
        foreach (var task in await tasks.ListReminderDueAsync(userId, today, await FilterAsync(userId, today, ct), track: true, ct))
        {
            var (dueSoon, overdue) = await DecideAsync(task, today, ct);
            if (dueSoon)
            {
                notifications.Add(TaskNotificationWriter.For(task, userId, NotificationKinds.TaskDueSoon, time.Now));
                task.LastDueSoonNotified = today;
            }
            if (overdue)
            {
                notifications.Add(TaskNotificationWriter.For(task, userId, NotificationKinds.TaskOverdue, time.Now));
                task.LastOverdueNotified = today;
            }
        }
    }

    /// <summary>把政策（每個模組的提醒天數、是否提醒逾期）解析成資料庫查詢能用的條件。</summary>
    private async Task<ReminderFilter> FilterAsync(string userId, DateOnly today, CancellationToken ct)
    {
        var dates = new HashSet<DateOnly>();
        var overdueModules = new List<long>();
        var overdueNoModule = false;
        foreach (var moduleId in await tasks.ListReminderModuleIdsAsync(userId, today, PolicyCatalog.TaskReminderLeadDays.Max, ct))
        {
            dates.Add(today.AddDays(await policies.IntAsync(PolicyCatalog.TaskReminderLeadDays, moduleId, ct)));
            var overdue = await policies.BoolAsync(PolicyCatalog.TaskRemindOverdue, moduleId, ct);
            if (!overdue) continue;
            if (moduleId is { } id) overdueModules.Add(id); else overdueNoModule = true;
        }
        return new ReminderFilter(dates, overdueModules, overdueNoModule);
    }

    private async Task<(bool DueSoon, bool Overdue)> DecideAsync(TaskItem task, DateOnly today, CancellationToken ct)
    {
        var due = task.DueDate!.Value;
        var dueSoon = false;
        var overdue = false;
        if (due >= today && task.LastDueSoonNotified != today)
            dueSoon = due == today.AddDays(await policies.IntAsync(PolicyCatalog.TaskReminderLeadDays, task.ModuleId, ct));
        if (due < today && task.LastOverdueNotified != today)
            overdue = await policies.BoolAsync(PolicyCatalog.TaskRemindOverdue, task.ModuleId, ct);
        return (dueSoon, overdue);
    }
}
