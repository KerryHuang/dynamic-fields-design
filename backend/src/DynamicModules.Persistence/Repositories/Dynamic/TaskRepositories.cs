using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Rules;
using DynamicModules.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class TaskRepository(DynamicDbContext db) : ITaskRepository
{
    private static IQueryable<TaskItem> Sorted(IQueryable<TaskItem> q) =>
        q.OrderBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenBy(t => t.Id);

    public Task<TaskItem?> GetAsync(long id, CancellationToken ct) =>
        db.Set<TaskItem>().Include(t => t.Participants).FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<bool> TryClaimVersionAsync(TaskItem task, CancellationToken ct)
    {
        var id = task.Id;
        var version = task.ModifiedTime;
        // 版本是 (ModifiedTime ?? CreatedTime)；CreatedTime 不會變，所以比對 ModifiedTime（含 null）即可。每次寫入都會 Touch，ModifiedTime 一定前進
        return await db.Set<TaskItem>().Where(t => t.Id == id && t.ModifiedTime == version)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ModifiedTime, t => t.ModifiedTime), ct) == 1;
    }

    public Task<TaskItem?> GetReadOnlyAsync(long id, CancellationToken ct) =>
        db.Set<TaskItem>().AsNoTracking().Include(t => t.Participants).FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<List<TaskItem>> ListByRecordAsync(long recordId, CancellationToken ct) =>
        Sorted(db.Set<TaskItem>().Include(t => t.Participants).Where(t => t.RecordId == recordId && t.ParentTaskId == null)).ToListAsync(ct);

    public Task<List<TaskItem>> ListChildrenAsync(long parentId, CancellationToken ct) =>
        db.Set<TaskItem>().Include(t => t.Participants).Where(t => t.ParentTaskId == parentId)
            .OrderBy(t => t.CreatedTime).ThenBy(t => t.Id).ToListAsync(ct);

    public Task<List<TaskItem>> ListByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToArray();
        return db.Set<TaskItem>().AsNoTracking().Include(t => t.Participants).Where(t => list.Contains(t.Id)).OrderBy(t => t.Id).ToListAsync(ct);
    }

    public Task<int> CountChildrenAsync(long parentId, CancellationToken ct) =>
        db.Set<TaskItem>().AsNoTracking().CountAsync(t => t.ParentTaskId == parentId, ct);

    public async Task<Dictionary<long, TaskProgress>> SubtaskProgressAsync(IReadOnlyCollection<long> parentIds, CancellationToken ct)
    {
        var ids = parentIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        var rows = await db.Set<TaskItem>().AsNoTracking()
            .Where(t => t.ParentTaskId != null && ids.Contains(t.ParentTaskId.Value))
            .GroupBy(t => new { Parent = t.ParentTaskId!.Value, t.Status }).Select(g => new { g.Key.Parent, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.Parent).ToDictionary(g => g.Key, g => new TaskProgress(
            g.Where(r => r.Status == TaskItemStatus.Done).Sum(r => r.Count),
            g.Where(r => r.Status == TaskItemStatus.Cancelled).Sum(r => r.Count),
            g.Sum(r => r.Count)));
    }

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
            TaskListTab.Subordinates => query.Where(SubordinateTasks.Of(q.Subordinates ?? new HashSet<string>())),
            _ => query.Where(t => db.Set<TaskParticipant>().Any(
                p => p.TaskId == t.Id && p.UserId == q.UserId && p.Reason == TaskParticipantReason.Mentioned)),
        };
        var open = new[] { TaskItemStatus.Todo, TaskItemStatus.InProgress };
        query = q.Filter switch
        {
            TaskListFilter.Open => query.Where(t => open.Contains(t.Status)),
            TaskListFilter.Overdue => query.Where(t => open.Contains(t.Status) && t.DueDate != null && t.DueDate < q.Today),
            TaskListFilter.Today => query.Where(t => open.Contains(t.Status) && t.DueDate == q.Today),
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

    public async Task<Dictionary<long, TaskRecordStub>> ListRecordStubsAsync(IReadOnlyCollection<long> recordIds, CancellationToken ct)
    {
        var ids = recordIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        return await db.Set<Record>().AsNoTracking().Where(r => ids.Contains(r.Id))
            .Select(r => new TaskRecordStub(r.Id, r.ModuleId, r.OwnerUserId, r.Title, r.DeleteFlag))
            .ToDictionaryAsync(s => s.Id, ct);
    }

    public Task<List<long>> ListVisibleIdsAsync(TaskVisibility visibility, CancellationToken ct) =>
        TaskVisibilityQuery.Visible(db, visibility).Select(t => t.Id).OrderBy(id => id).ToListAsync(ct);

    public Task<List<TaskItem>> ListForReportAsync(TaskVisibility visibility, TaskReportNarrowing narrowing, int take, CancellationToken ct)
    {
        IQueryable<TaskItem> query = TaskVisibilityQuery.Visible(db, visibility).Include(t => t.Participants);
        if (narrowing.Statuses is { } statuses)
        {
            var list = statuses.ToList();
            query = query.Where(t => list.Contains(t.Status));
        }
        if (narrowing is { DateField: { } field, From: { } from, ToInclusive: { } to })
        {
            // 牆上時間的半開區間，Kind Unspecified 與寫入一致（PostgreSQL timestamp without time zone）
            var start = from.ToDateTime(TimeOnly.MinValue);
            var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue);
            query = field switch
            {
                "due" => query.Where(t => t.DueDate >= from && t.DueDate <= to),
                "created" => query.Where(t => t.CreatedTime >= start && t.CreatedTime < end),
                "completed" => query.Where(t => t.CompletedTime >= start && t.CompletedTime < end),
                "createdOrCompleted" => query.Where(t => (t.CreatedTime >= start && t.CreatedTime < end) || (t.CompletedTime >= start && t.CompletedTime < end)),
                _ => throw new ArgumentException($"不支援的日期欄位：{field}", nameof(narrowing)),
            };
        }
        return query.OrderBy(t => t.Id).Take(take).ToListAsync(ct);
    }

    /// <summary>可見條件＋範圍＋篩選（看板設計 §5.7）。所有範圍都再 AND 可見條件（縱深防禦）。</summary>
    private IQueryable<TaskItem> BoardBase(TaskBoardQuery q)
    {
        var query = TaskVisibilityQuery.Visible(db, q.Visibility);
        if (q.TopLevelOnly) query = query.Where(t => t.ParentTaskId == null);
        var me = q.Visibility.UserId;
        query = q.Scope switch
        {
            TaskBoardConfig.ScopeAssigned => query.Where(t => t.AssigneeUserId == me),
            TaskBoardConfig.ScopeInvolved => query.Where(t => t.Participants.Any(p => p.UserId == me)),
            TaskBoardConfig.ScopeSubordinates => query.Where(SubordinateTasks.Of(q.Subordinates)),
            _ => query,
        };
        if (q.Statuses is { } statuses)
        {
            var list = statuses.ToArray();
            query = query.Where(t => list.Contains(t.Status));
        }
        if (q.AssigneeUserIds is { Count: > 0 } assignees)
        {
            var ids = assignees.ToArray();
            var unassigned = q.IncludeUnassigned == true;
            query = query.Where(t => (t.AssigneeUserId != null && ids.Contains(t.AssigneeUserId)) || (unassigned && t.AssigneeUserId == null));
        }
        else if (q.IncludeUnassigned == false) query = query.Where(t => t.AssigneeUserId != null);
        if (q.CreatorUserIds is { Count: > 0 } creators)
        {
            var ids = creators.ToArray();
            query = query.Where(t => t.CreatedBy != null && ids.Contains(t.CreatedBy));
        }
        if (q.Sources is { Count: > 0 } sources)
        {
            var personal = sources.Contains(TaskBoardConfig.SourcePersonal);
            var record = sources.Contains(TaskBoardConfig.SourceRecord);
            var rule = sources.Contains(TaskBoardConfig.SourceRule);
            query = query.Where(t => (personal && t.RecordId == null) || (record && t.RecordId != null && t.RuleId == null) || (rule && t.RuleId != null));
        }
        if (q.DueFrom is { } from && q.DueTo is { } to) query = query.Where(t => t.DueDate != null && t.DueDate >= from && t.DueDate <= to);
        if (q.Keyword is { Length: > 0 } keyword)
        {
            var k = keyword.ToLower();
            query = query.Where(t => t.Title.ToLower().Contains(k));
        }
        return query;
    }

    private static IQueryable<TaskItem> InColumn(IQueryable<TaskItem> query, TaskBoardQuery q, string column)
    {
        switch (q.GroupBy)
        {
            case TaskBoardConfig.ByStatus:
                if (!TaskBoardConfig.TryParseStatus(column, out var status)) throw new ArgumentException($"未知的狀態欄：{column}", nameof(column));
                return query.Where(t => t.Status == status);
            case TaskBoardConfig.ByDueBucket:
                return query.Where(TaskDueBuckets.Condition(column, q.Today, q.WeekEnd));
            default:
                if (column == TaskBoardConfig.Unassigned) return query.Where(t => t.AssigneeUserId == null);
                if (!column.StartsWith(TaskBoardConfig.UserColumnPrefix, StringComparison.Ordinal))
                    throw new ArgumentException($"未知的負責人欄：{column}", nameof(column));
                var userId = column[TaskBoardConfig.UserColumnPrefix.Length..];
                return query.Where(t => t.AssigneeUserId == userId);
        }
    }

    public async Task<Dictionary<string, int>> CountBoardColumnsAsync(TaskBoardQuery q, IReadOnlyCollection<string> columns, CancellationToken ct)
    {
        var query = BoardBase(q);
        switch (q.GroupBy)
        {
            case TaskBoardConfig.ByStatus:
                return (await query.GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
                    .ToDictionary(x => TaskBoardConfig.StatusKey(x.Key), x => x.Count);
            case TaskBoardConfig.ByAssignee:
                return (await query.GroupBy(t => t.AssigneeUserId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
                    .ToDictionary(x => x.Key is null ? TaskBoardConfig.Unassigned : TaskBoardConfig.UserColumnPrefix + x.Key, x => x.Count);
            default:
                var result = new Dictionary<string, int>();
                foreach (var column in columns)
                    if (await InColumn(query, q, column).CountAsync(ct) is > 0 and var n) result[column] = n;
                return result;
        }
    }

    public Task<List<TaskItem>> QueryBoardColumnAsync(TaskBoardQuery q, string column, int offset, int take, CancellationToken ct)
    {
        var query = InColumn(BoardBase(q), q, column);
        var desc = q.SortDescending;
        IOrderedQueryable<TaskItem> ordered = q.GroupBy == TaskBoardConfig.ByStatus && column == TaskBoardConfig.StatusKey(TaskItemStatus.Done)
            // 「完成」欄固定完成時間新到舊；null 明確排最後（PostgreSQL 的 DESC 預設把 null 排最前）
            ? query.OrderBy(t => t.CompletedTime == null).ThenByDescending(t => t.CompletedTime)
            : q.SortKey switch
            {
                "createdTime" => desc ? query.OrderByDescending(t => t.CreatedTime) : query.OrderBy(t => t.CreatedTime),
                "title" => desc ? query.OrderByDescending(t => t.Title) : query.OrderBy(t => t.Title),
                // 到期日：空的不論方向都排最後
                _ => desc ? query.OrderBy(t => t.DueDate == null).ThenByDescending(t => t.DueDate)
                    : query.OrderBy(t => t.DueDate == null).ThenBy(t => t.DueDate),
            };
        return ordered.ThenBy(t => t.Id).Include(t => t.Participants).Skip(offset).Take(take).ToListAsync(ct);
    }

    public async Task<(List<TaskItem> Items, int Total)> QueryGanttAsync(TaskBoardQuery q, DateOnly from, DateOnly to, int take, CancellationToken ct)
    {
        var query = BoardBase(q).Where(t => t.DueDate != null && t.DueDate >= from && (t.StartDate ?? t.DueDate) <= to);
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(t => t.StartDate ?? t.DueDate).ThenBy(t => t.DueDate).ThenBy(t => t.Id)
            .Include(t => t.Participants).Take(take).ToListAsync(ct);
        return (items, total);
    }

    public async Task<(List<TaskItem> Items, int Total)> QueryUnscheduledAsync(TaskBoardQuery q, int take, CancellationToken ct)
    {
        var query = BoardBase(q).Where(t => t.DueDate == null);
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(t => t.CreatedTime).ThenBy(t => t.Id).Include(t => t.Participants).Take(take).ToListAsync(ct);
        return (items, total);
    }

    public async Task<HashSet<long>> ListMatchingIdsAsync(TaskBoardQuery q, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToArray();
        if (list.Length == 0) return [];
        return (await BoardBase(q).Where(t => list.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct)).ToHashSet();
    }

    public async Task<Dictionary<long, (DateOnly From, DateOnly To)>> SubtaskSpanAsync(IReadOnlyCollection<long> parentIds, CancellationToken ct)
    {
        var ids = parentIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        var rows = await db.Set<TaskItem>().AsNoTracking()
            .Where(t => t.ParentTaskId != null && ids.Contains(t.ParentTaskId.Value) && t.Status != TaskItemStatus.Cancelled
                && (t.StartDate != null || t.DueDate != null))
            .GroupBy(t => t.ParentTaskId!.Value)
            .Select(g => new { Parent = g.Key, From = g.Min(t => t.StartDate ?? t.DueDate), To = g.Max(t => t.DueDate ?? t.StartDate) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Parent, r => (r.From!.Value, r.To!.Value));
    }

    public async Task<Dictionary<long, int>> CountCommentsAsync(IReadOnlyCollection<long> taskIds, CancellationToken ct)
    {
        var ids = taskIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        return await db.Set<TaskComment>().AsNoTracking()
            .Where(c => ids.Contains(c.TaskId) && c.Kind == TaskCommentKind.Comment && !c.Withdrawn)
            .GroupBy(c => c.TaskId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

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

    private IQueryable<TaskItem> ReminderBase(string userId)
    {
        var open = new[] { TaskItemStatus.Todo, TaskItemStatus.InProgress };
        // 掛在已關閉（凍結）模組記錄上的任務不提醒，模組重新開啟後恢復（設計 §16 第 6 條）
        return db.Set<TaskItem>().Where(t => t.AssigneeUserId == userId && open.Contains(t.Status) && t.DueDate != null
            && (t.ModuleId == null || !db.Set<Module>().Any(m => m.Id == t.ModuleId && m.Status == ModuleStatus.Closed)));
    }

    public Task<List<long?>> ListReminderModuleIdsAsync(string userId, DateOnly today, int maxLeadDays, CancellationToken ct)
    {
        var last = today.AddDays(maxLeadDays);
        return ReminderBase(userId).AsNoTracking()
            .Where(t => (t.DueDate >= today && t.DueDate <= last && t.LastDueSoonNotified == null)
                || (t.DueDate < today && t.LastOverdueNotified != today))
            .Select(t => t.ModuleId).Distinct().ToListAsync(ct);
    }

    private IQueryable<TaskItem> ReminderDue(string userId, DateOnly today, ReminderFilter filter)
    {
        var hasDueSoon = filter.DueSoonUntil is not null;
        var until = filter.DueSoonUntil ?? today;
        var overdueModules = filter.OverdueModuleIds.ToArray();
        var overdueNoModule = filter.OverdueWithoutModule;
        return ReminderBase(userId).Where(t => (hasDueSoon && t.DueDate >= today && t.DueDate <= until && t.LastDueSoonNotified == null)
            || (t.DueDate < today && t.LastOverdueNotified != today
                && (t.ModuleId == null ? overdueNoModule : overdueModules.Contains(t.ModuleId.Value))));
    }

    public Task<bool> AnyReminderDueAsync(string userId, DateOnly today, ReminderFilter filter, CancellationToken ct) =>
        ReminderDue(userId, today, filter).AsNoTracking().AnyAsync(ct);

    public Task<List<TaskItem>> ListReminderDueAsync(string userId, DateOnly today, ReminderFilter filter, CancellationToken ct) =>
        ReminderDue(userId, today, filter).AsNoTracking().OrderBy(t => t.Id).ToListAsync(ct);

    public async Task<bool> TryMarkDueSoonNotifiedAsync(TaskItem read, DateOnly today, CancellationToken ct) =>
        await SameVersion(read).Where(t => t.LastDueSoonNotified == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastDueSoonNotified, today), ct) == 1;

    public async Task<bool> TryMarkOverdueNotifiedAsync(TaskItem read, DateOnly today, CancellationToken ct) =>
        await SameVersion(read).Where(t => t.LastOverdueNotified == null || t.LastOverdueNotified != today)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastOverdueNotified, today), ct) == 1;

    /// <summary>
    /// 提醒判斷時讀到的那一版：負責人、到期日、狀態都沒變。沒有序列化時（PostgreSQL），並行的改派、改期、完成先提交就認領不到；
    /// 還沒提交時，這裡的 UPDATE 等它的列鎖，提交後重新比對條件同樣落空。
    /// </summary>
    private IQueryable<TaskItem> SameVersion(TaskItem read)
    {
        var assignee = read.AssigneeUserId;
        var due = read.DueDate;
        var status = read.Status;
        return db.Set<TaskItem>().Where(t => t.Id == read.Id && t.AssigneeUserId == assignee && t.DueDate == due && t.Status == status);
    }

    public void ResetReminderStamps(TaskItem task, bool overdueToo)
    {
        task.LastDueSoonNotified = null;
        if (overdueToo) task.LastOverdueNotified = null;
        var entry = db.Entry(task);
        if (entry.State is EntityState.Added or EntityState.Detached) return;
        // 只把 null 設回 null 時 EF 判定沒變、不送 UPDATE；強制寫入，蓋掉並行請求剛蓋上的戳記
        entry.Property(t => t.LastDueSoonNotified).IsModified = true;
        if (overdueToo) entry.Property(t => t.LastOverdueNotified).IsModified = true;
    }

    public Task<List<TaskItem>> ListByRuleOccurrenceAsync(long ruleId, long recordId, int occurrence, CancellationToken ct) =>
        db.Set<TaskItem>().Include(t => t.Participants)
            .Where(t => t.RuleId == ruleId && t.RecordId == recordId && t.RuleOccurrence == occurrence).OrderBy(t => t.Id).ToListAsync(ct);

    public Task<bool> AnyOpenByRuleAndRecordAsync(long ruleId, long recordId, CancellationToken ct) =>
        db.Set<TaskItem>().AnyAsync(t => t.RuleId == ruleId && t.RecordId == recordId
            && (t.Status == TaskItemStatus.Todo || t.Status == TaskItemStatus.InProgress), ct);

    public Task<List<TaskItem>> ListOpenByRuleAndRecordAsync(long ruleId, long recordId, CancellationToken ct) =>
        db.Set<TaskItem>().Include(t => t.Participants).Where(t => t.RuleId == ruleId && t.RecordId == recordId
            && (t.Status == TaskItemStatus.Todo || t.Status == TaskItemStatus.InProgress)).OrderBy(t => t.Id).ToListAsync(ct);

    public void Add(TaskItem task) => db.Set<TaskItem>().Add(task);

    public void Remove(TaskItem task) => db.Set<TaskItem>().Remove(task);
}

public class TaskRuleRepository(DynamicDbContext db) : ITaskRuleRepository
{
    private IQueryable<TaskRule> WithTasks(bool track = false)
    {
        var q = db.Set<TaskRule>().Include(r => r.Tasks.OrderBy(t => t.SortOrder).ThenBy(t => t.Id));
        return track ? q : q.AsNoTracking();
    }

    public Task<TaskRule?> GetByKeyAsync(string key, CancellationToken ct) =>
        WithTasks(track: true).FirstOrDefaultAsync(r => r.Key == key, ct);

    public Task<List<TaskRule>> ListByModuleAsync(long moduleId, CancellationToken ct) =>
        WithTasks().Where(r => r.ModuleId == moduleId).OrderBy(r => r.Key).ToListAsync(ct);

    public Task<List<TaskRule>> ListActiveByModuleAsync(long moduleId, CancellationToken ct) =>
        // 應用程式關閉時它的規則不再觸發，包括掛在共用主檔上的（設計 §5.1）；規則本身的 Active 不動，重新開啟就恢復
        WithTasks().Where(r => r.ModuleId == moduleId && r.Active
                && (r.AppId == null || db.Set<App>().Any(a => a.Id == r.AppId && a.Status == AppStatus.Open)))
            .OrderBy(r => r.Key).ToListAsync(ct);

    public Task<List<TaskRule>> ListByAppAsync(long appId, CancellationToken ct) =>
        WithTasks(track: true).Where(r => r.AppId == appId).OrderBy(r => r.Key).ToListAsync(ct);

    public Task<int> LockAsync(IReadOnlyCollection<long> ids, CancellationToken ct) =>
        ids.Count == 0
            ? Task.FromResult(0)
            : db.Set<TaskRule>().Where(r => ids.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.Key, r => r.Key), ct);

    public Task<TaskRule?> GetByIdAsync(long id, CancellationToken ct) =>
        WithTasks().FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<int?> GetLatestOccurrenceAsync(long ruleId, long recordId, CancellationToken ct) =>
        db.Set<TaskRuleRun>().AsNoTracking().Where(r => r.RuleId == ruleId && r.RecordId == recordId).MaxAsync(r => (int?)r.Occurrence, ct);

    public async Task<List<TaskRule>> ListReferencingFieldsAsync(IReadOnlyCollection<long> fieldIds, CancellationToken ct) =>
        (await WithTasks(track: true).OrderBy(r => r.Key).ToListAsync(ct))
            .Where(r => TaskRuleReferences.FieldIdsOf(r).Overlaps(fieldIds)).ToList();

    public void AddRun(TaskRuleRun run) => db.Set<TaskRuleRun>().Add(run);

    public void Add(TaskRule rule) => db.Set<TaskRule>().Add(rule);

    public void Remove(TaskRule rule) => db.Set<TaskRule>().Remove(rule);
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

    public Task<bool> AnyUnreadBySourceAsync(string userId, string sourceType, long sourceId, string kind, CancellationToken ct) =>
        db.Set<Notification>().AsNoTracking().AnyAsync(n => n.SourceType == sourceType && n.SourceId == sourceId
            && n.UserId == userId && n.Kind == kind && n.ReadTime == null, ct);
}
