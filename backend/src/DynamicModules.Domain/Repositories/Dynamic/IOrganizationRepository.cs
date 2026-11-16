using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>組織骨架：部門與使用者所屬部門。</summary>
public interface IOrganizationRepository
{
    /// <summary>全部部門，不追蹤，依 SortOrder、Code 排序。</summary>
    Task<List<Department>> ListAsync(CancellationToken ct);

    Task<Department?> GetTrackedAsync(long id, CancellationToken ct);

    Task<bool> CodeExistsAsync(string code, long? excludeId, CancellationToken ct);

    Task<int> CountChildrenAsync(long id, CancellationToken ct);

    Task<int> CountMembersAsync(long id, CancellationToken ct);

    /// <summary>UserId → DepartmentId。</summary>
    Task<Dictionary<string, long>> ListUserDepartmentsAsync(CancellationToken ct);

    /// <summary>departmentId 為 null 時移除；寫入由 IUnitOfWork 提交。</summary>
    Task SetUserDepartmentAsync(string userId, long? departmentId, CancellationToken ct);

    void Add(Department d);

    void Remove(Department d);
}
