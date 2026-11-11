using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.NotificationService;

/// <summary>
/// 任務事件寫站內通知（設計 §6.1）。一律不通知這次操作的人自己（自己指派給自己、自己 @ 自己、自己留言、自己完成）。
/// 指派、提到的收件人固定；留言、完成、取消的收件角色由政策 task.notify.* 決定（依任務所屬模組），
/// 留言再扣掉這則留言已 @ 到的人（他們已收到「提到你」）。系統建立的任務沒有建立者。
/// </summary>
public sealed class TaskNotificationWriter(INotificationRepository notifications, IBusinessTime time, IPolicyReader policies) : ITaskNotifier
{
    public const string SourceType = "task";

    public async Task NotifyAsync(IReadOnlyList<TaskChange> changes, CancellationToken ct)
    {
        foreach (var c in changes)
            foreach (var userId in (await RecipientsAsync(c, ct)).Where(u => u != c.ActorUserId).Distinct(StringComparer.Ordinal))
                notifications.Add(For(c.Task, userId, KindOf(c.Kind), time.Now));
    }

    /// <summary>給提醒等其他寫入端共用同一個格式。</summary>
    public static Notification For(TaskItem task, string userId, string kind, DateTime now) => new()
    {
        UserId = userId, Kind = kind, TitleSnapshot = task.Title, Link = $"/tasks?open={task.Id}",
        SourceType = SourceType, SourceId = task.Id, CreatedTime = now
    };

    private async Task<IEnumerable<string>> RecipientsAsync(TaskChange c, CancellationToken ct)
    {
        if (c.Kind is TaskChangeKind.Assigned or TaskChangeKind.Mentioned) return c.UserId is null ? [] : [c.UserId];
        var def = c.Kind switch
        {
            TaskChangeKind.Commented => PolicyCatalog.TaskNotifyCommented,
            TaskChangeKind.Completed => PolicyCatalog.TaskNotifyCompleted,
            _ => PolicyCatalog.TaskNotifyCancelled,
        };
        var roles = await policies.ListAsync(def, c.Task.ModuleId, ct);
        var users = roles.Select(r => r == "assignee" ? c.Task.AssigneeUserId : c.Task.CreatedBy).OfType<string>();
        // 已 @ 到的人（扣掉留言者本人）收「提到你」就好，不再收「新留言」
        return c.Kind == TaskChangeKind.Commented
            ? users.Where(u => !(c.Mentioned ?? []).Where(m => m != c.ActorUserId).Contains(u))
            : users;
    }

    private static string KindOf(TaskChangeKind k) => k switch
    {
        TaskChangeKind.Assigned => NotificationKinds.TaskAssigned,
        TaskChangeKind.Mentioned => NotificationKinds.TaskMentioned,
        TaskChangeKind.Commented => NotificationKinds.TaskCommented,
        TaskChangeKind.Completed => NotificationKinds.TaskCompleted,
        _ => NotificationKinds.TaskCancelled,
    };
}
