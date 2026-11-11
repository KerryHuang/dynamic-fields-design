using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class ModulePolicyRepository(DynamicDbContext db) : IModulePolicyRepository
{
    public Task<List<ModulePolicy>> ListByModuleAsync(long moduleId, CancellationToken ct) =>
        db.Set<ModulePolicy>().AsNoTracking().Where(p => p.ModuleId == moduleId).ToListAsync(ct);

    public Task<ModulePolicy?> GetTrackedAsync(long moduleId, string code, CancellationToken ct) =>
        db.Set<ModulePolicy>().FirstOrDefaultAsync(p => p.ModuleId == moduleId && p.Code == code, ct);

    public void Add(ModulePolicy policy) => db.Set<ModulePolicy>().Add(policy);

    public void Remove(ModulePolicy policy) => db.Set<ModulePolicy>().Remove(policy);
}
