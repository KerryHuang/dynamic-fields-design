using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.NotificationService;

/// <summary>
/// 任務的到期提醒（任務設計 §6.2；維運設計 §10.2.9），讀通知與背景排程兩條路徑共用。只提醒負責人、未完成的任務。「今天」是營業日。
/// <list type="bullet">
/// <item>即將到期（taskDueSoon）：今天 ≤ 到期日 ≤ 今天＋提醒天數（政策 task.reminderLeadDays，含到期當天），每個任務只補一次
/// （LastDueSoonNotified 有值就不再補）；改到期日或換負責人後重新計，完成或取消後重開不重新計。</item>
/// <item>已逾期（taskOverdue）：該模組開啟逾期提醒（政策 task.remindOverdue）時每天最多一則；上一則逾期通知還沒讀就只蓋今天的戳記、不再新增。</item>
/// </list>
/// 去重靠戳記的條件式更新（影響列數 1 才新增通知）：兩條路徑或多個實例同時跑也不會重複，不依賴寫入排隊。
/// 認領時一併比對讀到的負責人、到期日、狀態：讀取後才被改派、改期或完成的任務不認領，通知只發給認領成功那一版的負責人。
/// </summary>
public sealed class TaskReminderSource(ITaskRepository tasks, INotificationRepository notifications, IBusinessTime time, IPolicyReader policies)
    : INotificationReminderSource
{
    public async Task<bool> AnyDueAsync(string userId, DateOnly today, CancellationToken ct)
    {
        var filter = await FilterAsync(userId, today, ct);
        // 先用資料庫端 EXISTS 擋掉絕大多數輪詢；成立時筆數已很小，再逐筆確認（各模組的提醒天數取最大值預篩，可能多選到別的模組的任務）
        if (!await tasks.AnyReminderDueAsync(userId, today, filter, ct)) return false;
        foreach (var task in await tasks.ListReminderDueAsync(userId, today, filter, ct))
            if (await DecideAsync(task, today, ct) is (true, _) or (_, true)) return true;
        return false;
    }

    public async Task AddDueAsync(string userId, DateOnly today, CancellationToken ct)
    {
        foreach (var task in await tasks.ListReminderDueAsync(userId, today, await FilterAsync(userId, today, ct), ct))
        {
            var (dueSoon, overdue) = await DecideAsync(task, today, ct);
            if (dueSoon && await tasks.TryMarkDueSoonNotifiedAsync(task, today, ct))
                notifications.Add(TaskNotificationWriter.For(task, userId, NotificationKinds.TaskDueSoon, time.Now));
            // 搶到今天的戳記才查未讀；還有沒讀的逾期通知時由它代表今天的提醒，讀過之後隔天才再補
            if (overdue && await tasks.TryMarkOverdueNotifiedAsync(task, today, ct)
                && !await notifications.AnyUnreadBySourceAsync(userId, TaskNotificationWriter.SourceType, task.Id, NotificationKinds.TaskOverdue, ct))
                notifications.Add(TaskNotificationWriter.For(task, userId, NotificationKinds.TaskOverdue, time.Now));
        }
    }

    /// <summary>把政策（每個模組的提醒天數、是否提醒逾期）解析成資料庫查詢能用的條件。</summary>
    private async Task<ReminderFilter> FilterAsync(string userId, DateOnly today, CancellationToken ct)
    {
        DateOnly? until = null;
        var overdueModules = new List<long>();
        var overdueNoModule = false;
        foreach (var moduleId in await tasks.ListReminderModuleIdsAsync(userId, today, PolicyCatalog.TaskReminderLeadDays.Max, ct))
        {
            var last = today.AddDays(await policies.IntAsync(PolicyCatalog.TaskReminderLeadDays, moduleId, ct));
            if (until is null || last > until) until = last;
            var overdue = await policies.BoolAsync(PolicyCatalog.TaskRemindOverdue, moduleId, ct);
            if (!overdue) continue;
            if (moduleId is { } id) overdueModules.Add(id); else overdueNoModule = true;
        }
        return new ReminderFilter(until, overdueModules, overdueNoModule);
    }

    private async Task<(bool DueSoon, bool Overdue)> DecideAsync(TaskItem task, DateOnly today, CancellationToken ct)
    {
        var due = task.DueDate!.Value;
        var dueSoon = false;
        var overdue = false;
        if (due >= today && task.LastDueSoonNotified is null)
            dueSoon = due <= today.AddDays(await policies.IntAsync(PolicyCatalog.TaskReminderLeadDays, task.ModuleId, ct));
        if (due < today && task.LastOverdueNotified != today)
            overdue = await policies.BoolAsync(PolicyCatalog.TaskRemindOverdue, task.ModuleId, ct);
        return (dueSoon, overdue);
    }
}
