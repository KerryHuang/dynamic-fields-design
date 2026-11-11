using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.TaskService;

public sealed record TaskInput(string Title, string? Description, string? AssigneeUserId, DateOnly? DueDate);

/// <summary>不含任何記錄欄位值；Record 依檢視者的權限遮蔽（設計 §7）。</summary>
public sealed record TaskDetailResponse(
    long Id, string Title, string? Description, TaskItemStatus Status, string? AssigneeUserId, string? AssigneeName, bool AssigneeActive,
    DateOnly? DueDate, bool Overdue, TaskRecordRef? Record, string? CreatedBy, string? CreatedByName, DateTime CreatedTime,
    DateTime? CompletedTime, string? RuleKey, bool RuleDeleted, bool CanEdit, bool CanComment, bool CanDelete, bool Frozen);

public sealed record TaskSummaryResponse(
    long Id, string Title, TaskItemStatus Status, string? AssigneeUserId, string? AssigneeName, bool AssigneeActive,
    DateOnly? DueDate, bool Overdue, TaskRecordRef? Record, bool FromRule, bool CanEdit);

/// <summary>ManualRules：這筆記錄可手動套用的任務規則（啟用中、manual、應用程式開啟中）。</summary>
public sealed record RecordTasksResponse(IReadOnlyList<TaskSummaryResponse> Items, bool CanAdd, IReadOnlyList<ManualRuleResponse> ManualRules);

public sealed record ManualRuleResponse(string Key, I18nText Name, TaskRuleRepeat Repeat, bool Applied, bool Applicable, bool PreviousOpen);

public sealed record TaskSummaryCounts(int DueToday, int Overdue);

public sealed record MentionRef(string UserId, string DisplayName, bool Active);

public sealed record TaskAttachmentResponse(long Id, string FileName, string ContentType, long Size);

/// <summary>已撤回的留言：Body 是 null、Mentions 與 Attachments 是空清單。系統記錄由前端依 SystemEvent＋SystemData 組文字。</summary>
public sealed record TaskCommentResponse(long Id, TaskCommentKind Kind, string? AuthorUserId, string? AuthorName, string? Body,
    bool Withdrawn, string? SystemEvent, JsonElement? SystemData, DateTime CreatedTime, IReadOnlyList<MentionRef> Mentions,
    IReadOnlyList<TaskAttachmentResponse> Attachments, bool CanWithdraw);

internal static class TaskViews
{
    public static bool Overdue(TaskItem t, DateOnly today) => t.IsOpen && t.DueDate is { } d && d < today;

    public static TaskDetailResponse ToDetail(TaskItem t, TaskPermissions p, IReadOnlyDictionary<string, AppUser> users,
        DateOnly today, string? ruleKey)
    {
        var assignee = t.AssigneeUserId is null ? null : users.GetValueOrDefault(t.AssigneeUserId);
        var creator = t.CreatedBy is null ? null : users.GetValueOrDefault(t.CreatedBy);
        return new TaskDetailResponse(
            t.Id, t.Title, t.Description, t.Status, t.AssigneeUserId, assignee?.DisplayName, assignee is { IsActive: true },
            t.DueDate, Overdue(t, today), p.Record, t.CreatedBy, creator?.DisplayName, t.CreatedTime, t.CompletedTime,
            ruleKey, t.RuleId is not null && ruleKey is null, p.CanEdit, p.CanComment, p.CanDelete, p.Frozen);
    }

    public static TaskSummaryResponse ToSummary(TaskItem t, TaskPermissions p, IReadOnlyDictionary<string, AppUser> users, DateOnly today)
    {
        var assignee = t.AssigneeUserId is null ? null : users.GetValueOrDefault(t.AssigneeUserId);
        return new TaskSummaryResponse(t.Id, t.Title, t.Status, t.AssigneeUserId, assignee?.DisplayName, assignee is { IsActive: true },
            t.DueDate, Overdue(t, today), p.Record, t.RuleId is not null, p.CanEdit);
    }

    /// <summary>
    /// 負責人異動的系統記錄只存使用者帳號（from／to）；讀取時補上 fromName、fromActive、toName、toActive，
    /// 前端據此顯示「名稱（已停用）」。帳號是空的或查無此人時不補該組欄位；原有欄位照舊，不改資料格式。
    /// </summary>
    private static JsonElement WithAssigneeNames(JsonElement original, IReadOnlyDictionary<string, AppUser> users)
    {
        if (original.ValueKind != JsonValueKind.Object) return original.Clone();
        var node = new Dictionary<string, JsonElement>();
        foreach (var p in original.EnumerateObject()) node[p.Name] = p.Value.Clone();
        foreach (var side in new[] { "from", "to" })
        {
            if (!node.TryGetValue(side, out var id) || id.ValueKind != JsonValueKind.String) continue;
            if (!users.TryGetValue(id.GetString()!, out var u)) continue;
            node[side + "Name"] = JsonSerializer.SerializeToElement(u.DisplayName);
            node[side + "Active"] = JsonSerializer.SerializeToElement(u.IsActive);
        }
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>Mentions：內文裡存在的使用者都列出（含已停用），不存在的不列。</summary>
    public static TaskCommentResponse ToComment(TaskComment c, IReadOnlyDictionary<string, AppUser> users, bool canWithdraw)
    {
        var author = c.AuthorUserId is null ? null : users.GetValueOrDefault(c.AuthorUserId);
        var human = c.Kind == TaskCommentKind.Comment;
        var visible = human && !c.Withdrawn;
        IReadOnlyList<MentionRef> mentions = visible
            ? TaskMentions.UserIds(c.Body).Select(id => users.GetValueOrDefault(id)).OfType<AppUser>()
                .Select(u => new MentionRef(u.UserId, u.DisplayName, u.IsActive)).ToList()
            : [];
        IReadOnlyList<TaskAttachmentResponse> attachments = c.Withdrawn
            ? []
            : c.Attachments.OrderBy(a => a.Id).Select(a => new TaskAttachmentResponse(a.Id, a.FileName, a.ContentType, a.Size)).ToList();
        JsonElement? data = null;
        if (c.SystemData is not null)
        {
            using var doc = JsonDocument.Parse(c.SystemData);
            data = c.SystemEvent == TaskSystemEvents.AssigneeChanged ? WithAssigneeNames(doc.RootElement, users) : doc.RootElement.Clone();
        }
        return new TaskCommentResponse(c.Id, c.Kind, c.AuthorUserId, author?.DisplayName, c.Withdrawn ? null : c.Body,
            c.Withdrawn, c.SystemEvent, data, c.CreatedTime, mentions, attachments, canWithdraw && human && !c.Withdrawn);
    }
}
