namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>站內通知（平台共用）：只帶標題快照與站內連結，不帶欄位值。SourceType／SourceId 指回來源（例如 task）。</summary>
public class Notification
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string TitleSnapshot { get; set; } = "";
    public string Link { get; set; } = "";
    public string? SourceType { get; set; }
    public long? SourceId { get; set; }
    public DateTime? ReadTime { get; set; }
    public DateTime CreatedTime { get; set; }
}

public static class NotificationKinds
{
    public const string TaskAssigned = "taskAssigned";
    public const string TaskMentioned = "taskMentioned";
    public const string TaskCommented = "taskCommented";
    public const string TaskDueSoon = "taskDueSoon";
    public const string TaskOverdue = "taskOverdue";
    public const string TaskCompleted = "taskCompleted";
    public const string TaskCancelled = "taskCancelled";
}
