using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface IListViewRepository
{
    /// <summary>追蹤中的檢視，供修改或刪除。</summary>
    Task<ListView?> GetAsync(long id, CancellationToken ct);
    /// <summary>共用檢視加上該使用者的個人檢視，共用優先、再依 Id（不追蹤）。</summary>
    Task<List<ListView>> ListVisibleAsync(long moduleId, string userId, CancellationToken ct);
    /// <summary>同一個模組、同一個範圍（ownerUserId 為 null 是共用）的檢視（追蹤中）。</summary>
    Task<List<ListView>> ListByScopeAsync(long moduleId, string? ownerUserId, CancellationToken ct);
    void Add(ListView view);
    void Remove(ListView view);
}
