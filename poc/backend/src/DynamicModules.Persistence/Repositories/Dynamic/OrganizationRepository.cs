using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class OrganizationRepository(DynamicDbContext db) : IOrganizationRepository
{
    public Task<List<Department>> ListAsync(CancellationToken ct) =>
        db.Set<Department>().AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Code).ToListAsync(ct);

    public Task<Department?> GetTrackedAsync(long id, CancellationToken ct) =>
        db.Set<Department>().FirstOrDefaultAsync(d => d.Id == id, ct);

    public Task<bool> CodeExistsAsync(string code, long? excludeId, CancellationToken ct) =>
        db.Set<Department>().AnyAsync(d => d.Code == code && d.Id != excludeId, ct);

    public Task<int> CountChildrenAsync(long id, CancellationToken ct) =>
        db.Set<Department>().CountAsync(d => d.ParentId == id, ct);

    public Task<int> CountMembersAsync(long id, CancellationToken ct) =>
        db.Set<UserDepartment>().CountAsync(m => m.DepartmentId == id, ct);

    public Task<Dictionary<string, long>> ListUserDepartmentsAsync(CancellationToken ct) =>
        db.Set<UserDepartment>().AsNoTracking().ToDictionaryAsync(m => m.UserId, m => m.DepartmentId, ct);

    public async Task SetUserDepartmentAsync(string userId, long? departmentId, CancellationToken ct)
    {
        var set = db.Set<UserDepartment>();
        var row = await set.FirstOrDefaultAsync(m => m.UserId == userId, ct);
        if (departmentId is null)
        {
            if (row is not null) set.Remove(row);
        }
        else if (row is null) set.Add(new UserDepartment { UserId = userId, DepartmentId = departmentId.Value });
        else row.DepartmentId = departmentId.Value;
    }

    public void Add(Department d) => db.Set<Department>().Add(d);

    public void Remove(Department d) => db.Set<Department>().Remove(d);
}
