using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>任務檢視（看板）與分享。</summary>
public interface ITaskViewRepository
{
    /// <summary>追蹤，含分享。</summary>
    Task<TaskView?> GetAsync(long id, CancellationToken ct);

    /// <summary>
    /// 不追蹤，含分享，依 Id：這個人擁有的、分享給他本人、他所在的任一群組、他的直屬部門的；
    /// includeAllShared 時另含所有有分享的（系統管理員）。
    /// </summary>
    Task<List<TaskView>> ListVisibleAsync(string kind, string userId, IReadOnlyCollection<long> groupIds, long? departmentId,
        bool includeAllShared, CancellationToken ct);

    /// <summary>清掉指向某個對象的分享（刪群組、刪部門時）；由 IUnitOfWork 提交。</summary>
    Task RemoveSharesToAsync(string targetKind, string targetId, CancellationToken ct);

    void Add(TaskView view);
    void Remove(TaskView view);
}
