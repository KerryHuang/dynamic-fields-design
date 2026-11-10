using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.NotificationService;

/// <summary>
/// 任務事件寫站內通知（設計 §6.1）。一律不通知這次操作的人自己（自己指派給自己、自己 @ 自己、自己留言、自己完成）。
/// 留言：負責人與建立者，扣掉這則留言已 @ 到的人（他們已收到「提到你」）。完成／取消：建立者（系統建立的不通知）。
/// </summary>
public sealed class TaskNotificationWriter(INotificationRepository notifications, IBusinessTime time) : ITaskNotifier
{
    public const string SourceType = "task";

    public Task NotifyAsync(IReadOnlyList<TaskChange> changes, CancellationToken ct)
    {
        foreach (var c in changes)
            foreach (var userId in Recipients(c).Where(u => u != c.ActorUserId).Distinct(StringComparer.Ordinal))
                notifications.Add(For(c.Task, userId, KindOf(c.Kind), time.Now));
        return Task.CompletedTask;
    }

    /// <summary>給提醒等其他寫入端共用同一個格式。</summary>
    public static Notification For(TaskItem task, string userId, string kind, DateTime now) => new()
    {
        UserId = userId, Kind = kind, TitleSnapshot = task.Title, Link = $"/tasks?open={task.Id}",
        SourceType = SourceType, SourceId = task.Id, CreatedTime = now
    };

    private static IEnumerable<string> Recipients(TaskChange c) => c.Kind switch
    {
        TaskChangeKind.Assigned or TaskChangeKind.Mentioned => c.UserId is null ? [] : [c.UserId],
        // 已 @ 到的人（扣掉留言者本人）收「提到你」就好，不再收「新留言」
        TaskChangeKind.Commented => new[] { c.Task.AssigneeUserId, c.Task.CreatedBy }.OfType<string>()
            .Where(u => !(c.Mentioned ?? []).Where(m => m != c.ActorUserId).Contains(u)),
        _ => c.Task.CreatedBy is null ? [] : [c.Task.CreatedBy],
    };

    private static string KindOf(TaskChangeKind k) => k switch
    {
        TaskChangeKind.Assigned => NotificationKinds.TaskAssigned,
        TaskChangeKind.Mentioned => NotificationKinds.TaskMentioned,
        TaskChangeKind.Commented => NotificationKinds.TaskCommented,
        TaskChangeKind.Completed => NotificationKinds.TaskCompleted,
        _ => NotificationKinds.TaskCancelled,
    };
}
