using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.TaskService;

/// <summary>SubtasksDone：子任務全部收尾，Task 是父任務（看板設計 §10.7.4 第 2 項）。</summary>
public enum TaskChangeKind { Assigned, Mentioned, Commented, Completed, Cancelled, SubtasksDone }

/// <summary>
/// 一筆要通知的任務變更。ActorUserId 是這次操作的使用者（規則或系統做的也一樣），通知端靠它判斷「不通知自己」。
/// UserId：Assigned 的新負責人；Mentioned：被 @ 的人。
/// </summary>
public sealed record TaskChange(TaskChangeKind Kind, TaskItem Task, string ActorUserId, string? UserId = null, IReadOnlyList<string>? Mentioned = null);

public interface ITaskNotifier
{
    Task NotifyAsync(IReadOnlyList<TaskChange> changes, CancellationToken ct);
}
