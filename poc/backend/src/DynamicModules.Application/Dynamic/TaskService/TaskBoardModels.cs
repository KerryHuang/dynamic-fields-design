using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.TaskService;

/// <summary>看板卡片；Record 依檢視者的權限遮蔽（看不到記錄時只有 Accessible=false，§5.9）。</summary>
public sealed record TaskCardResponse(
    long Id, string Title, TaskItemStatus Status, string? AssigneeUserId, string? AssigneeName, bool AssigneeActive,
    DateOnly? DueDate, bool Overdue, bool DueToday, TaskRecordRef? Record, bool FromRule, bool Frozen, int CommentCount,
    DateTime? CompletedTime, string Version, bool CanEdit, bool CanReassign);

/// <summary>
/// Kind：status、dueBucket、user、unassigned。UserId、Name、Inactive 只有 user 欄有。Total 是整欄總數（不受已載入張數限制）；
/// Truncated 代表總數超過畫面上限 500 張；Droppable=false 時 DropReason 說明原因。
/// </summary>
public sealed record TaskBoardColumnResponse(string Key, string Kind, string? UserId, string? Name, bool? Inactive, int Total,
    IReadOnlyList<TaskCardResponse> Cards, bool HasMore, bool Truncated, bool Droppable, string? DropReason);

/// <summary>
/// Today、WeekEnd 由伺服器算（營運時區、政策 calendar.weekStart），LaterDefault 是「之後」欄的預設日期（下週最後一天）。
/// ScopeAvailable=false：範圍是「部屬的任務」但目前沒有部屬或政策關閉（D7）。OmittedAssignees：超過 50 欄而沒列出的負責人數。
/// </summary>
public sealed record TaskBoardResponse(DateOnly Today, DateOnly WeekEnd, DateOnly LaterDefault, bool ScopeAvailable,
    IReadOnlyList<TaskBoardColumnResponse> Columns, int OmittedAssignees);

public sealed record TaskBoardPageResponse(string Key, int Total, IReadOnlyList<TaskCardResponse> Cards, bool HasMore, bool Truncated);

/// <summary>Visible=false：移動後操作者看不到了，不回卡片；改負責人造成的才附一次性的復原憑證（J10）。</summary>
public sealed record TaskMoveResponse(bool Visible, TaskCardResponse? Card, string? UndoToken);
