using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface ITaskRepository
{
    /// <summary>追蹤，含參與者。</summary>
    Task<TaskItem?> GetAsync(long id, CancellationToken ct);
    /// <summary>追蹤，含參與者；DueDate 升冪、null 最後、再依 Id。</summary>
    Task<List<TaskItem>> ListByRecordAsync(long recordId, CancellationToken ct);
    /// <summary>追蹤，含參與者。</summary>
    Task<List<TaskItem>> ListByRecordIdsAsync(IReadOnlyCollection<long> recordIds, CancellationToken ct);
    /// <summary>不追蹤，含參與者；排序同 <see cref="ListByRecordAsync"/>。</summary>
    Task<(List<TaskItem> Items, int Total)> QueryMineAsync(TaskListQuery q, CancellationToken ct);
    /// <summary>指派給這個人、未完成的任務中，今天到期與已逾期的數量。</summary>
    Task<(int DueToday, int Overdue)> CountMineDueAsync(string userId, DateOnly today, CancellationToken ct);
    Task<List<TaskParticipant>> ListParticipantsAsync(long taskId, CancellationToken ct);
    Task<bool> IsParticipantAsync(long taskId, string userId, CancellationToken ct);
    /// <summary>含軟刪除的記錄；不存在時回 null。</summary>
    Task<TaskRecordStub?> GetRecordStubAsync(long recordId, CancellationToken ct);
    /// <summary>規則已刪除時回 null。</summary>
    Task<string?> GetRuleKeyAsync(long ruleId, CancellationToken ct);
    /// <summary>這個模組的任務留言附件的儲存鍵（模組清除時刪檔用）。</summary>
    Task<List<string>> ListAttachmentKeysByModuleAsync(long moduleId, CancellationToken ct);
    /// <summary>不追蹤，含附件；CreatedTime、Id 升冪。</summary>
    Task<List<TaskComment>> ListCommentsAsync(long taskId, CancellationToken ct);
    /// <summary>追蹤，含附件。</summary>
    Task<TaskComment?> GetCommentAsync(long taskId, long commentId, CancellationToken ct);
    /// <summary>未撤回留言的附件儲存鍵（附件清掃把它們算成被參照）。</summary>
    Task<HashSet<string>> ListLiveAttachmentKeysAsync(CancellationToken ct);
    /// <summary>不追蹤；附件須屬於該留言與任務，留言已撤回時回 null。</summary>
    Task<TaskCommentAttachment?> GetAttachmentAsync(long taskId, long commentId, long attachmentId, CancellationToken ct);
    /// <summary>
    /// 負責人是 userId、未完成、DueDate 不為 null，而且
    /// (DueDate == today+1 且 LastDueSoonNotified != today) 或 (DueDate &lt; today 且 LastOverdueNotified != today)。
    /// </summary>
    Task<bool> AnyReminderDueAsync(string userId, DateOnly today, CancellationToken ct);
    /// <summary>條件同 <see cref="AnyReminderDueAsync"/>；追蹤。</summary>
    Task<List<TaskItem>> ListReminderDueAsync(string userId, DateOnly today, CancellationToken ct);
    void Add(TaskItem task);
    void Remove(TaskItem task);
}

/// <summary>站內通知（設計 §6）。只碰自己的通知：每個方法都帶 userId。</summary>
public interface INotificationRepository
{
    void Add(Notification n);
    /// <summary>不追蹤；CreatedTime、Id 降冪。</summary>
    Task<List<Notification>> ListLatestAsync(string userId, int take, CancellationToken ct);
    Task<int> CountUnreadAsync(string userId, CancellationToken ct);
    /// <summary>追蹤；不是這個人的通知回 null。</summary>
    Task<Notification?> GetOwnAsync(long id, string userId, CancellationToken ct);
    Task MarkAllReadAsync(string userId, DateTime now, CancellationToken ct);
    Task<bool> AnyOlderThanAsync(string userId, DateTime cutoff, CancellationToken ct);
    /// <summary>刪掉 CreatedTime 早於 cutoff 的通知。</summary>
    Task PurgeOlderThanAsync(string userId, DateTime cutoff, CancellationToken ct);
    /// <summary>來源被刪除時，刪掉所有人指向它的通知。</summary>
    Task RemoveBySourceAsync(string sourceType, long sourceId, CancellationToken ct);
}

public sealed record TaskRecordStub(long Id, long ModuleId, string OwnerUserId, string Title, bool Deleted);

public enum TaskListTab { Assigned, Created, Mentioned }

public enum TaskListFilter { Open, Overdue, ThisWeek, All }

public sealed record TaskListQuery(string UserId, TaskListTab Tab, TaskListFilter Filter, DateOnly Today, DateOnly WeekEnd, int Page, int Size);
