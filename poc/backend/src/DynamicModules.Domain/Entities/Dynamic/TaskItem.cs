namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>
/// 平台共用的任務（核心資料表 Dynamic_Task）。ModuleId 與 RecordId 同時有值（記錄任務）或同時為空（個人任務）。
/// 不繼承 AuditableEntity：CreatedBy／ModifiedBy 為 null 代表系統。設計見 docs/superpowers/specs/2026-11-10-task-management-design.md §3。
/// </summary>
public class TaskItem
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? AssigneeUserId { get; set; }
    public DateOnly? DueDate { get; set; }
    public TaskItemStatus Status { get; set; } = TaskItemStatus.Todo;

    public long? ModuleId { get; set; }
    public long? RecordId { get; set; }
    public string? RecordTitleSnapshot { get; set; }

    public long? RuleId { get; set; }
    public int? RuleOccurrence { get; set; }
    public string? RuleTaskCode { get; set; }
    public bool DueOverridden { get; set; }
    public bool AssigneeOverridden { get; set; }
    public bool CancelledByRule { get; set; }

    public string? CreatedBy { get; set; }
    public DateTime CreatedTime { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTime? ModifiedTime { get; set; }
    public DateTime? CompletedTime { get; set; }
    public DateOnly? LastDueSoonNotified { get; set; }
    public DateOnly? LastOverdueNotified { get; set; }

    public List<TaskComment> Comments { get; set; } = [];
    public List<TaskParticipant> Participants { get; set; } = [];

    public bool IsOpen => Status is TaskItemStatus.Todo or TaskItemStatus.InProgress;
}

public enum TaskItemStatus { Todo, InProgress, Done, Cancelled }

/// <summary>留言串的一則：人寫的留言（Comment），或系統記錄（System，前端依 SystemEvent＋SystemData 組文字）。</summary>
public class TaskComment
{
    public long Id { get; set; }
    public long TaskId { get; set; }
    public TaskCommentKind Kind { get; set; }
    public string? AuthorUserId { get; set; }
    public string Body { get; set; } = "";
    public string? SystemEvent { get; set; }
    /// <summary>JSON 文字，例如 {"from":…,"to":…}。</summary>
    public string? SystemData { get; set; }
    public DateTime CreatedTime { get; set; }
    public bool Withdrawn { get; set; }
    public string? WithdrawnBy { get; set; }
    public DateTime? WithdrawnTime { get; set; }
    public List<TaskCommentAttachment> Attachments { get; set; } = [];
}

public enum TaskCommentKind { Comment, System }

/// <summary>留言附件；檔案沿用既有附件儲存（IAttachmentStore），StorageKey 是儲存鍵。</summary>
public class TaskCommentAttachment
{
    public long Id { get; set; }
    public long CommentId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public string StorageKey { get; set; } = "";
    public DateTime CreatedTime { get; set; }
}

/// <summary>任務的參與者：決定誰看得到任務、誰收新留言通知。主鍵 (TaskId, UserId, Reason)。</summary>
public class TaskParticipant
{
    public long TaskId { get; set; }
    public string UserId { get; set; } = "";
    public TaskParticipantReason Reason { get; set; }
}

public enum TaskParticipantReason { Creator, Assignee, Mentioned }

/// <summary>系統記錄的事件代碼（TaskComment.SystemEvent）。</summary>
public static class TaskSystemEvents
{
    public const string Created = "created";
    public const string AssigneeChanged = "assigneeChanged";
    public const string DueChanged = "dueChanged";
    public const string StatusChanged = "statusChanged";
    public const string ConditionUnmet = "conditionUnmet";
    public const string ConditionRestored = "conditionRestored";
    public const string RecordDeleted = "recordDeleted";
    public const string RetriggerSkipped = "retriggerSkipped";
}
