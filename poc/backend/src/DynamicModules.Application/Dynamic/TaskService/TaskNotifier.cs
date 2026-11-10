using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.TaskService;

public enum TaskChangeKind { Assigned, Mentioned, Commented, Completed, Cancelled }

/// <summary>
/// 一筆要通知的任務變更。ActorUserId 是這次操作的使用者（規則或系統做的也一樣），通知端靠它判斷「不通知自己」。
/// UserId：Assigned 的新負責人；Mentioned：被 @ 的人。
/// </summary>
public sealed record TaskChange(TaskChangeKind Kind, TaskItem Task, string ActorUserId, string? UserId = null, IReadOnlyList<string>? Mentioned = null);

public interface ITaskNotifier
{
    Task NotifyAsync(IReadOnlyList<TaskChange> changes, CancellationToken ct);
}

/// <summary>站內通知上線前的空實作。</summary>
public sealed class NullTaskNotifier : ITaskNotifier
{
    public Task NotifyAsync(IReadOnlyList<TaskChange> changes, CancellationToken ct) => Task.CompletedTask;
}
