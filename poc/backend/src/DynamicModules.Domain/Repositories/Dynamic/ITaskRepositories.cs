using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Tasks;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface ITaskRepository
{
    /// <summary>追蹤，含參與者。</summary>
    Task<TaskItem?> GetAsync(long id, CancellationToken ct);
    /// <summary>不追蹤，含參與者；只讀的檢查（例如上傳資格）用，避免把舊值留在追蹤器裡讓後面的處理器讀到。</summary>
    Task<TaskItem?> GetReadOnlyAsync(long id, CancellationToken ct);
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
    /// <summary>一次取多筆記錄 stub（含軟刪除）；不存在的記錄不在結果裡。</summary>
    Task<Dictionary<long, TaskRecordStub>> ListRecordStubsAsync(IReadOnlyCollection<long> recordIds, CancellationToken ct);
    /// <summary>符合可見條件的任務 Id（單一資料庫查詢，條件與 <c>TaskAccess</c> 的逐筆判斷一致）。</summary>
    Task<List<long>> ListVisibleIdsAsync(TaskVisibility visibility, CancellationToken ct);
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
    /// 可能要提醒的任務所屬的模組（null 代表沒有所屬模組），不重複：負責人是 userId、未完成、DueDate 不為 null、所屬模組沒關閉，而且
    /// (today &lt;= DueDate &lt;= today+maxLeadDays 且 LastDueSoonNotified != today) 或 (DueDate &lt; today 且 LastOverdueNotified != today)。
    /// 呼叫端用它依政策算出 <see cref="ReminderFilter"/>。
    /// </summary>
    Task<List<long?>> ListReminderModuleIdsAsync(string userId, DateOnly today, int maxLeadDays, CancellationToken ct);
    /// <summary>有沒有符合 <paramref name="filter"/> 的提醒任務（資料庫端 EXISTS）。</summary>
    Task<bool> AnyReminderDueAsync(string userId, DateOnly today, ReminderFilter filter, CancellationToken ct);
    /// <summary>條件同 <see cref="AnyReminderDueAsync"/>，依 Id 排序；track 為 true 時追蹤。</summary>
    Task<List<TaskItem>> ListReminderDueAsync(string userId, DateOnly today, ReminderFilter filter, bool track, CancellationToken ct);
    /// <summary>這條規則在這筆記錄第 occurrence 次觸發產生的任務；追蹤，含參與者。</summary>
    Task<List<TaskItem>> ListByRuleOccurrenceAsync(long ruleId, long recordId, int occurrence, CancellationToken ct);
    /// <summary>這條規則在這筆記錄上是否還有未完成（待辦、進行中）的任務，不分第幾次觸發。</summary>
    Task<bool> AnyOpenByRuleAndRecordAsync(long ruleId, long recordId, CancellationToken ct);
    /// <summary>同上，回傳這些未完成的任務（追蹤）。</summary>
    Task<List<TaskItem>> ListOpenByRuleAndRecordAsync(long ruleId, long recordId, CancellationToken ct);
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

/// <summary>任務規則（設計 §5）。含 Tasks 的方法都依 SortOrder 排序。</summary>
public interface ITaskRuleRepository
{
    /// <summary>追蹤，含 Tasks。</summary>
    Task<TaskRule?> GetByKeyAsync(string key, CancellationToken ct);
    /// <summary>不追蹤，含 Tasks，依 Key。</summary>
    Task<List<TaskRule>> ListByModuleAsync(long moduleId, CancellationToken ct);
    /// <summary>不追蹤，含 Tasks，只列啟用中、且所屬應用程式（有的話）是開啟中的規則，依 Key。</summary>
    Task<List<TaskRule>> ListActiveByModuleAsync(long moduleId, CancellationToken ct);
    /// <summary>追蹤，含 Tasks。</summary>
    Task<List<TaskRule>> ListByAppAsync(long appId, CancellationToken ct);
    /// <summary>不追蹤，含 Tasks。</summary>
    Task<TaskRule?> GetByIdAsync(long id, CancellationToken ct);
    /// <summary>這筆記錄最近一次觸發這條規則的次數（Occurrence）；沒有觸發過回 null。</summary>
    Task<int?> GetLatestOccurrenceAsync(long ruleId, long recordId, CancellationToken ct);
    /// <summary>觸發條件、任務的負責人或到期基準、條件引用了任一欄位的規則；追蹤，含 Tasks。</summary>
    Task<List<TaskRule>> ListReferencingFieldsAsync(IReadOnlyCollection<long> fieldIds, CancellationToken ct);
    void AddRun(TaskRuleRun run);
    void Add(TaskRule rule);
    void Remove(TaskRule rule);
}

public sealed record TaskRecordStub(long Id, long ModuleId, string OwnerUserId, string Title, bool Deleted);

public enum TaskListTab { Assigned, Created, Mentioned, Subordinates }

public enum TaskListFilter { Open, Overdue, Today, ThisWeek, All }

/// <param name="Subordinates">Tab 為 Subordinates 時的部屬名單（負責人在名單內，或未指派且建立者在名單內）；其他分頁忽略。</param>
public sealed record TaskListQuery(string UserId, TaskListTab Tab, TaskListFilter Filter, DateOnly Today, DateOnly WeekEnd, int Page, int Size,
    IReadOnlySet<string>? Subordinates = null);

/// <summary>
/// 提醒條件（由政策算出）：到期日在 <see cref="DueSoonDates"/> 內的任務是「即將到期」候選；
/// 已逾期的任務只有所屬模組開啟逾期提醒（沒有所屬模組的看 <see cref="OverdueWithoutModule"/>）才算。
/// <see cref="DueSoonDates"/> 是各模組「今天＋提醒天數」的聯集，只做資料庫端預先篩選，逐筆仍由呼叫端依該任務模組的政策再判斷。
/// </summary>
public sealed record ReminderFilter(IReadOnlyCollection<DateOnly> DueSoonDates, IReadOnlyCollection<long> OverdueModuleIds, bool OverdueWithoutModule);
