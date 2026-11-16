using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface IReportRepository
{
    /// <summary>追蹤中的報表（含 Shares），供修改或刪除。</summary>
    Task<Report?> GetAsync(long id, CancellationToken ct);
    /// <summary>該使用者擁有，或分享給 groupIds 任一群組的報表（含 Shares，依 Id、不追蹤）。</summary>
    Task<List<Report>> ListVisibleAsync(string userId, IReadOnlyCollection<long> groupIds, CancellationToken ct);
    /// <summary>所有至少分享給一個群組的報表（含 Shares，依 Id、不追蹤），系統管理員用。</summary>
    Task<List<Report>> ListSharedAsync(CancellationToken ct);
    /// <summary>是否已有同名報表（不分擁有者），Demo 產生前的衝突檢查用。</summary>
    Task<bool> NameExistsAsync(string name, CancellationToken ct);
    void Add(Report report);
    void Remove(Report report);
}
