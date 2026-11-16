namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>
/// 存起來的任務檢視（核心資料表 Dynamic_TaskView）：看板，之後的甘特圖共用同一張表（看板設計 §6.1）。
/// ConfigJson 的格式見 <see cref="Tasks.TaskBoardConfig"/>；分享只決定看不看得到設定，不授予任務權限（§6.2）。
/// </summary>
public class TaskView : AuditableEntity
{
    public long Id { get; set; }
    /// <summary><see cref="TaskViewKinds"/>。</summary>
    public string Kind { get; set; } = TaskViewKinds.Board;
    public string OwnerUserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ConfigJson { get; set; } = "{}";
    /// <summary>GUID 字串，每次寫入都換新值（同 Report）。</summary>
    public string RowVersion { get; set; } = "";
    public List<TaskViewShare> Shares { get; set; } = [];
}

/// <summary>分享對象；TargetId 是使用者帳號或群組、部門的 Id 字串。對象沒有 FK，刪群組、刪部門時明確清除。</summary>
public class TaskViewShare
{
    public long ViewId { get; set; }
    /// <summary><see cref="TaskViewShareKinds"/>。</summary>
    public string TargetKind { get; set; } = "";
    public string TargetId { get; set; } = "";
}

public static class TaskViewKinds
{
    public const string Board = "board";
    public const string Gantt = "gantt";
}

public static class TaskViewShareKinds
{
    public const string User = "user";
    public const string Group = "group";
    public const string Department = "department";
}
