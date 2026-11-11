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
        foreach (var task in await Candidates(userId, today, track: false, ct))
            if (await DecideAsync(task, today, ct) is (true, _) or (_, true)) return true;
        return false;
    }

    public async Task AddDueAsync(string userId, DateOnly today, CancellationToken ct)
    {
        foreach (var task in await Candidates(userId, today, track: true, ct))
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

    private Task<List<TaskItem>> Candidates(string userId, DateOnly today, bool track, CancellationToken ct) =>
        tasks.ListReminderCandidatesAsync(userId, today, PolicyCatalog.TaskReminderLeadDays.Max, track, ct);

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
