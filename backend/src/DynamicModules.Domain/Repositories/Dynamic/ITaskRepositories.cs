using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Tasks;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>
/// 任務報表在資料庫端的縮小條件（看板設計 §7.7）：只放證明精確的條件，記憶體路徑照舊重套全部條件。
/// Statuses 為 null 不限狀態、空集合是一筆都不要。DateField 是 due|created|completed|createdOrCompleted，null 不縮小日期；
/// From、ToInclusive 是營運日期，含頭含尾。
/// </summary>
public sealed record TaskReportNarrowing(IReadOnlyCollection<TaskItemStatus>? Statuses, string? DateField, DateOnly? From, DateOnly? ToInclusive);

public interface ITaskRepository
{
    /// <summary>追蹤，含參與者。</summary>
    Task<TaskItem?> GetAsync(long id, CancellationToken ct);
    /// <summary>
    /// 條件式更新認領版本：以讀到的 ModifiedTime 比對資料列，相符才算成功（列被鎖到交易結束，另一個同版本的請求會等到這筆交易結束，再比對失敗）。
    /// 回 false 代表資料列已被別人改過，呼叫端要整筆回滾。
    /// </summary>
    Task<bool> TryClaimVersionAsync(TaskItem task, CancellationToken ct);
    /// <summary>不追蹤，含參與者；只讀的檢查（例如上傳資格）用，避免把舊值留在追蹤器裡讓後面的處理器讀到。</summary>
    Task<TaskItem?> GetReadOnlyAsync(long id, CancellationToken ct);
    /// <summary>只列頂層任務（子任務在父任務詳細裡看，看板設計 §10.7.2）；追蹤，含參與者；DueDate 升冪、null 最後、再依 Id。</summary>
    Task<List<TaskItem>> ListByRecordAsync(long recordId, CancellationToken ct);
    /// <summary>這個父任務底下的子任務；追蹤，含參與者；CreatedTime、Id 升冪。</summary>
    Task<List<TaskItem>> ListChildrenAsync(long parentId, CancellationToken ct);
    /// <summary>一次取多筆任務（不追蹤，含參與者）；不存在的不在結果裡。</summary>
    Task<List<TaskItem>> ListByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken ct);
    /// <summary>這個父任務底下現有的子任務數（含已完成、已取消）。</summary>
    Task<int> CountChildrenAsync(long parentId, CancellationToken ct);
    /// <summary>一次算多個父任務的子任務數（GROUP BY 父任務、狀態）；沒有子任務的不在結果裡。</summary>
    Task<Dictionary<long, TaskProgress>> SubtaskProgressAsync(IReadOnlyCollection<long> parentIds, CancellationToken ct);
    /// <summary>追蹤，含參與者。</summary>
    Task<List<TaskItem>> ListByRecordIdsAsync(IReadOnlyCollection<long> recordIds, CancellationToken ct);
    /// <summary>不追蹤，含參與者；排序同 <see cref="ListByRecordAsync"/>。</summary>
    Task<(List<TaskItem> Items, int Total)> QueryMineAsync(TaskListQuery q, CancellationToken ct);
    /// <summary>指派給這個人、未完成的任務中，今天到期與已逾期的數量。</summary>
    Task<(int DueToday, int Overdue)> CountMineDueAsync(string userId, DateOnly today, CancellationToken ct);
    Task<List<TaskParticipant>> ListParticipantsAsync(long taskId, CancellationToken ct);
    Task<bool> IsParticipantAsync(long taskId, string userId, CancellationToken ct);
    /// <summary>一次取多筆記錄 stub（含軟刪除）；不存在的記錄不在結果裡。</summary>
    Task<Dictionary<long, TaskRecordStub>> ListRecordStubsAsync(IReadOnlyCollection<long> recordIds, CancellationToken ct);
    /// <summary>符合可見條件的任務 Id（單一資料庫查詢，條件與 <c>TaskAccess</c> 的逐筆判斷一致）。</summary>
    Task<List<long>> ListVisibleIdsAsync(TaskVisibility visibility, CancellationToken ct);
    /// <summary>任務報表的列：可見條件＋縮小條件，不追蹤、含參與者，依 Id；最多 take 筆。</summary>
    Task<List<TaskItem>> ListForReportAsync(TaskVisibility visibility, TaskReportNarrowing narrowing, int take, CancellationToken ct);
    /// <summary>
    /// 看板各欄的任務數（可見條件＋範圍＋篩選＋欄條件）。狀態看板一次 GROUP BY 狀態、負責人看板一次 GROUP BY 負責人（回所有有任務的負責人，
    /// 鍵 user:&lt;帳號&gt; 與 unassigned，忽略 columns），到期區間看板每欄一次 COUNT；沒有任務的欄不在結果裡。
    /// </summary>
    Task<Dictionary<string, int>> CountBoardColumnsAsync(TaskBoardQuery q, IReadOnlyCollection<string> columns, CancellationToken ct);
    /// <summary>某一欄的一頁任務（不追蹤，含參與者）；排序照設定，「完成」欄固定完成時間新到舊，最後依 Id。</summary>
    Task<List<TaskItem>> QueryBoardColumnAsync(TaskBoardQuery q, string column, int offset, int take, CancellationToken ct);
    /// <summary>
    /// 甘特圖時間軸列（看板設計 §11.5.2.5、§11.5.3）：<see cref="TaskBoardQuery"/> 的可見條件＋範圍＋篩選（不讀 GroupBy、SortKey），
    /// 且 DueDate 不為 null、[StartDate ?? DueDate, DueDate] 與 [from, to]（含頭含尾）相交。不追蹤、含參與者；
    /// 依 StartDate ?? DueDate、DueDate、Id 遞增，最多 take 筆；Total 是全部符合的筆數。
    /// </summary>
    Task<(List<TaskItem> Items, int Total)> QueryGanttAsync(TaskBoardQuery q, DateOnly from, DateOnly to, int take, CancellationToken ct);
    /// <summary>甘特圖未排程：條件同上但只取 DueDate 為 null（不看時間窗）；依 CreatedTime、Id 遞增，最多 take 筆。</summary>
    Task<(List<TaskItem> Items, int Total)> QueryUnscheduledAsync(TaskBoardQuery q, int take, CancellationToken ct);
    /// <summary>ids 裡符合 <see cref="TaskBoardQuery"/> 可見條件＋範圍＋篩選的 Id（甘特圖判斷父任務是否被篩選擋掉）。</summary>
    Task<HashSet<long>> ListMatchingIdsAsync(TaskBoardQuery q, IReadOnlyCollection<long> ids, CancellationToken ct);
    /// <summary>
    /// 子任務跨度（GT-T15）：一次 GROUP BY 父任務，排除已取消、兩個日期都沒有的子任務；區間是
    /// MIN(StartDate ?? DueDate) 到 MAX(DueDate ?? StartDate)。沒有可算子任務的父任務不在結果裡。不看操作者的可見條件，呼叫端只能傳看得到的父任務。
    /// </summary>
    Task<Dictionary<long, (DateOnly From, DateOnly To)>> SubtaskSpanAsync(IReadOnlyCollection<long> parentIds, CancellationToken ct);
    /// <summary>人寫的、未撤回的留言數；沒有留言的任務不在結果裡。</summary>
    Task<Dictionary<long, int>> CountCommentsAsync(IReadOnlyCollection<long> taskIds, CancellationToken ct);
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
    /// (today &lt;= DueDate &lt;= today+maxLeadDays 且 LastDueSoonNotified 為 null) 或 (DueDate &lt; today 且 LastOverdueNotified != today)。
    /// 呼叫端用它依政策算出 <see cref="ReminderFilter"/>。
    /// </summary>
    Task<List<long?>> ListReminderModuleIdsAsync(string userId, DateOnly today, int maxLeadDays, CancellationToken ct);
    /// <summary>有沒有符合 <paramref name="filter"/> 的提醒任務（資料庫端 EXISTS）。</summary>
    Task<bool> AnyReminderDueAsync(string userId, DateOnly today, ReminderFilter filter, CancellationToken ct);
    /// <summary>條件同 <see cref="AnyReminderDueAsync"/>，依 Id 排序，不追蹤。</summary>
    Task<List<TaskItem>> ListReminderDueAsync(string userId, DateOnly today, ReminderFilter filter, CancellationToken ct);
    /// <summary>
    /// 條件式蓋「即將到期」戳記：只在還沒提醒過（LastDueSoonNotified 為 null），而且負責人、到期日、狀態仍和 <paramref name="read"/>
    /// （判斷時讀到的那一版）相同時寫入 today，影響列數 1 才回 true。讀取之後任務被改派、改期或完成，就不認領，留給下一輪重新判斷。
    /// </summary>
    Task<bool> TryMarkDueSoonNotifiedAsync(TaskItem read, DateOnly today, CancellationToken ct);
    /// <summary>條件式蓋「逾期」戳記：只在今天還沒蓋過、而且負責人、到期日、狀態仍和 <paramref name="read"/> 相同時寫入 today，影響列數 1 才回 true。</summary>
    Task<bool> TryMarkOverdueNotifiedAsync(TaskItem read, DateOnly today, CancellationToken ct);
    /// <summary>
    /// 改到期日或換負責人後重新計提醒（O1 計畫裁決 D2）：清掉「即將到期」戳記，overdueToo 時連「逾期」戳記一起清；
    /// 一定寫進這次存檔（值本來就是 null 也照寫，蓋掉並行請求剛蓋上的戳記）。
    /// </summary>
    void ResetReminderStamps(TaskItem task, bool overdueToo);
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
    /// <summary>這個人有沒有指向這個來源、這種通知、還沒讀的通知。</summary>
    Task<bool> AnyUnreadBySourceAsync(string userId, string sourceType, long sourceId, string kind, CancellationToken ct);
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
/// 看板欄查詢的條件（看板設計 §5.7）：一律先套 <see cref="Visibility"/>，再套範圍與篩選。GroupBy、Scope、SortKey 的值見 <see cref="TaskBoardConfig"/>。
/// </summary>
/// <param name="Subordinates">Scope 為 subordinates 時的部屬名單（空集合＝沒有結果）。</param>
/// <param name="Statuses">null 代表不篩狀態。</param>
/// <param name="Sources">personal、record、rule；null 或空代表不篩。</param>
/// <param name="TopLevelOnly">看板設定 showSubtasks=false：只列父任務與獨立任務。</param>
public sealed record TaskBoardQuery(TaskVisibility Visibility, string GroupBy, string Scope, IReadOnlySet<string> Subordinates,
    IReadOnlyCollection<TaskItemStatus>? Statuses, IReadOnlyCollection<string>? AssigneeUserIds, bool? IncludeUnassigned,
    IReadOnlyCollection<string>? CreatorUserIds, IReadOnlyCollection<string>? Sources, DateOnly? DueFrom, DateOnly? DueTo, string? Keyword,
    DateOnly Today, DateOnly WeekEnd, string SortKey, bool SortDescending, bool TopLevelOnly = false);

/// <summary>
/// 提醒條件（由政策算出）：到期日在今天到 <see cref="DueSoonUntil"/> 之間、還沒提醒過的任務是「即將到期」候選（null 代表沒有候選）；
/// 已逾期的任務只有所屬模組開啟逾期提醒（沒有所屬模組的看 <see cref="OverdueWithoutModule"/>）才算。
/// <see cref="DueSoonUntil"/> 是各模組「今天＋提醒天數」的最大值，只做資料庫端預先篩選，逐筆仍由呼叫端依該任務模組的政策再判斷。
/// </summary>
public sealed record ReminderFilter(DateOnly? DueSoonUntil, IReadOnlyCollection<long> OverdueModuleIds, bool OverdueWithoutModule);
