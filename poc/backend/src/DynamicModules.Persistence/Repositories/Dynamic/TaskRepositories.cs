using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class TaskRepository(DynamicDbContext db) : ITaskRepository
{
    private static IQueryable<TaskItem> Sorted(IQueryable<TaskItem> q) =>
        q.OrderBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenBy(t => t.Id);

    public Task<TaskItem?> GetAsync(long id, CancellationToken ct) =>
        db.Set<TaskItem>().Include(t => t.Participants).FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<TaskItem?> GetReadOnlyAsync(long id, CancellationToken ct) =>
        db.Set<TaskItem>().AsNoTracking().Include(t => t.Participants).FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<List<TaskItem>> ListByRecordAsync(long recordId, CancellationToken ct) =>
        Sorted(db.Set<TaskItem>().Include(t => t.Participants).Where(t => t.RecordId == recordId)).ToListAsync(ct);

    public Task<List<TaskItem>> ListByRecordIdsAsync(IReadOnlyCollection<long> recordIds, CancellationToken ct)
    {
        var ids = recordIds.ToList();
        return db.Set<TaskItem>().Include(t => t.Participants)
            .Where(t => t.RecordId != null && ids.Contains(t.RecordId.Value)).OrderBy(t => t.Id).ToListAsync(ct);
    }

    public async Task<(List<TaskItem> Items, int Total)> QueryMineAsync(TaskListQuery q, CancellationToken ct)
    {
        var query = db.Set<TaskItem>().AsNoTracking();
        query = q.Tab switch
        {
            TaskListTab.Assigned => query.Where(t => t.AssigneeUserId == q.UserId),
            TaskListTab.Created => query.Where(t => t.CreatedBy == q.UserId),
            _ => query.Where(t => db.Set<TaskParticipant>().Any(
                p => p.TaskId == t.Id && p.UserId == q.UserId && p.Reason == TaskParticipantReason.Mentioned)),
        };
        var open = new[] { TaskItemStatus.Todo, TaskItemStatus.InProgress };
        query = q.Filter switch
        {
            TaskListFilter.Open => query.Where(t => open.Contains(t.Status)),
            TaskListFilter.Overdue => query.Where(t => open.Contains(t.Status) && t.DueDate != null && t.DueDate < q.Today),
            TaskListFilter.ThisWeek => query.Where(t => open.Contains(t.Status) && t.DueDate != null && t.DueDate >= q.Today && t.DueDate <= q.WeekEnd),
            _ => query,
        };
        var total = await query.CountAsync(ct);
        var items = await Sorted(query).Include(t => t.Participants)
            .Skip((Math.Max(q.Page, 1) - 1) * q.Size).Take(q.Size).ToListAsync(ct);
        return (items, total);
    }

    public async Task<(int DueToday, int Overdue)> CountMineDueAsync(string userId, DateOnly today, CancellationToken ct)
    {
        var open = new[] { TaskItemStatus.Todo, TaskItemStatus.InProgress };
        var mine = db.Set<TaskItem>().AsNoTracking()
            .Where(t => t.AssigneeUserId == userId && open.Contains(t.Status) && t.DueDate != null);
        var dueToday = await mine.CountAsync(t => t.DueDate == today, ct);
        var overdue = await mine.CountAsync(t => t.DueDate < today, ct);
        return (dueToday, overdue);
    }

    public Task<List<TaskParticipant>> ListParticipantsAsync(long taskId, CancellationToken ct) =>
        db.Set<TaskParticipant>().AsNoTracking().Where(p => p.TaskId == taskId).OrderBy(p => p.UserId).ThenBy(p => p.Reason).ToListAsync(ct);

    public Task<bool> IsParticipantAsync(long taskId, string userId, CancellationToken ct) =>
        db.Set<TaskParticipant>().AsNoTracking().AnyAsync(p => p.TaskId == taskId && p.UserId == userId, ct);

    public Task<TaskRecordStub?> GetRecordStubAsync(long recordId, CancellationToken ct) =>
        db.Set<Record>().AsNoTracking().Where(r => r.Id == recordId)
            .Select(r => new TaskRecordStub(r.Id, r.ModuleId, r.OwnerUserId, r.Title, r.DeleteFlag)).FirstOrDefaultAsync(ct);

    public Task<string?> GetRuleKeyAsync(long ruleId, CancellationToken ct) =>
        db.Set<TaskRule>().AsNoTracking().Where(r => r.Id == ruleId).Select(r => (string?)r.Key).FirstOrDefaultAsync(ct);

    public Task<List<string>> ListAttachmentKeysByModuleAsync(long moduleId, CancellationToken ct) =>
        db.Set<TaskCommentAttachment>().AsNoTracking()
            .Where(a => db.Set<TaskComment>().Any(c => c.Id == a.CommentId
                && db.Set<TaskItem>().Any(t => t.Id == c.TaskId && t.ModuleId == moduleId)))
            .OrderBy(a => a.Id).Select(a => a.StorageKey).ToListAsync(ct);

    public Task<List<TaskComment>> ListCommentsAsync(long taskId, CancellationToken ct) =>
        db.Set<TaskComment>().AsNoTracking().Include(c => c.Attachments).Where(c => c.TaskId == taskId)
            .OrderBy(c => c.CreatedTime).ThenBy(c => c.Id).ToListAsync(ct);

    public Task<TaskComment?> GetCommentAsync(long taskId, long commentId, CancellationToken ct) =>
        db.Set<TaskComment>().Include(c => c.Attachments).FirstOrDefaultAsync(c => c.Id == commentId && c.TaskId == taskId, ct);

    public async Task<HashSet<string>> ListLiveAttachmentKeysAsync(CancellationToken ct) =>
        (await db.Set<TaskCommentAttachment>().AsNoTracking()
            .Where(a => db.Set<TaskComment>().Any(c => c.Id == a.CommentId && !c.Withdrawn))
            .Select(a => a.StorageKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

    public Task<TaskCommentAttachment?> GetAttachmentAsync(long taskId, long commentId, long attachmentId, CancellationToken ct) =>
        db.Set<TaskCommentAttachment>().AsNoTracking()
            .Where(a => a.Id == attachmentId && a.CommentId == commentId
                && db.Set<TaskComment>().Any(c => c.Id == commentId && c.TaskId == taskId && !c.Withdrawn))
            .FirstOrDefaultAsync(ct);

    private IQueryable<TaskItem> ReminderDue(string userId, DateOnly today)
    {
        var open = new[] { TaskItemStatus.Todo, TaskItemStatus.InProgress };
        var tomorrow = today.AddDays(1);
        // 掛在已關閉（凍結）模組記錄上的任務不提醒，模組重新開啟後恢復（設計 §16 第 6 條）
        return db.Set<TaskItem>().Where(t => t.AssigneeUserId == userId && open.Contains(t.Status) && t.DueDate != null
            && (t.ModuleId == null || !db.Set<Module>().Any(m => m.Id == t.ModuleId && m.Status == ModuleStatus.Closed))
            && ((t.DueDate == tomorrow && t.LastDueSoonNotified != today) || (t.DueDate < today && t.LastOverdueNotified != today)));
    }

    public Task<bool> AnyReminderDueAsync(string userId, DateOnly today, CancellationToken ct) =>
        ReminderDue(userId, today).AsNoTracking().AnyAsync(ct);

    public Task<List<TaskItem>> ListReminderDueAsync(string userId, DateOnly today, CancellationToken ct) =>
        ReminderDue(userId, today).OrderBy(t => t.Id).ToListAsync(ct);

    public void Add(TaskItem task) => db.Set<TaskItem>().Add(task);

    public void Remove(TaskItem task) => db.Set<TaskItem>().Remove(task);
}

public class NotificationRepository(DynamicDbContext db) : INotificationRepository
{
    public void Add(Notification n) => db.Set<Notification>().Add(n);

    public Task<List<Notification>> ListLatestAsync(string userId, int take, CancellationToken ct) =>
        db.Set<Notification>().AsNoTracking().Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedTime).ThenByDescending(n => n.Id).Take(take).ToListAsync(ct);

    public Task<int> CountUnreadAsync(string userId, CancellationToken ct) =>
        db.Set<Notification>().AsNoTracking().CountAsync(n => n.UserId == userId && n.ReadTime == null, ct);

    public Task<Notification?> GetOwnAsync(long id, string userId, CancellationToken ct) =>
        db.Set<Notification>().FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId, ct);

    public Task MarkAllReadAsync(string userId, DateTime now, CancellationToken ct) =>
        db.Set<Notification>().Where(n => n.UserId == userId && n.ReadTime == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadTime, now), ct);

    public Task<bool> AnyOlderThanAsync(string userId, DateTime cutoff, CancellationToken ct) =>
        db.Set<Notification>().AsNoTracking().AnyAsync(n => n.UserId == userId && n.CreatedTime < cutoff, ct);

    public Task PurgeOlderThanAsync(string userId, DateTime cutoff, CancellationToken ct) =>
        db.Set<Notification>().Where(n => n.UserId == userId && n.CreatedTime < cutoff).ExecuteDeleteAsync(ct);

    public Task RemoveBySourceAsync(string sourceType, long sourceId, CancellationToken ct) =>
        db.Set<Notification>().Where(n => n.SourceType == sourceType && n.SourceId == sourceId).ExecuteDeleteAsync(ct);
}
