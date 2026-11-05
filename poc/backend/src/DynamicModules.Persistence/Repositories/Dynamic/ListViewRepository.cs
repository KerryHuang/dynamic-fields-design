using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class ListViewRepository(DynamicDbContext db) : IListViewRepository
{
    public Task<ListView?> GetAsync(long id, CancellationToken ct) =>
        db.Set<ListView>().FirstOrDefaultAsync(v => v.Id == id, ct);

    public Task<List<ListView>> ListVisibleAsync(long moduleId, string userId, CancellationToken ct) =>
        db.Set<ListView>().AsNoTracking()
            .Where(v => v.ModuleId == moduleId && (v.OwnerUserId == null || v.OwnerUserId == userId))
            .OrderBy(v => v.OwnerUserId != null).ThenBy(v => v.Id)
            .ToListAsync(ct);

    public Task<List<ListView>> ListByScopeAsync(long moduleId, string? ownerUserId, CancellationToken ct) =>
        db.Set<ListView>().Where(v => v.ModuleId == moduleId && v.OwnerUserId == ownerUserId).ToListAsync(ct);

    public void Add(ListView view) => db.Set<ListView>().Add(view);

    public void Remove(ListView view) => db.Set<ListView>().Remove(view);
}
