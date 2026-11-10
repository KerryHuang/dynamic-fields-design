using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.NotificationService;

/// <summary>
/// 任務的到期提醒（設計 §6.2）：負責人、未完成、明天到期（taskDueSoon）或已逾期（taskOverdue），
/// 記在 LastDueSoonNotified／LastOverdueNotified，同一任務同一種提醒一天最多一則。「明天」「今天」都是營業日。
/// </summary>
public sealed class TaskReminderSource(ITaskRepository tasks, INotificationRepository notifications, IBusinessTime time)
    : INotificationReminderSource
{
    public Task<bool> AnyDueAsync(string userId, DateOnly today, CancellationToken ct) => tasks.AnyReminderDueAsync(userId, today, ct);

    public async Task AddDueAsync(string userId, DateOnly today, CancellationToken ct)
    {
        foreach (var task in await tasks.ListReminderDueAsync(userId, today, ct))
        {
            if (task.DueDate == today.AddDays(1) && task.LastDueSoonNotified != today)
            {
                notifications.Add(TaskNotificationWriter.For(task, userId, NotificationKinds.TaskDueSoon, time.Now));
                task.LastDueSoonNotified = today;
            }
            if (task.DueDate < today && task.LastOverdueNotified != today)
            {
                notifications.Add(TaskNotificationWriter.For(task, userId, NotificationKinds.TaskOverdue, time.Now));
                task.LastOverdueNotified = today;
            }
        }
    }
}
