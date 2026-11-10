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
    void Add(TaskItem task);
    void Remove(TaskItem task);
}

public sealed record TaskRecordStub(long Id, long ModuleId, string OwnerUserId, string Title, bool Deleted);

public enum TaskListTab { Assigned, Created, Mentioned }

public enum TaskListFilter { Open, Overdue, ThisWeek, All }

public sealed record TaskListQuery(string UserId, TaskListTab Tab, TaskListFilter Filter, DateOnly Today, DateOnly WeekEnd, int Page, int Size);
