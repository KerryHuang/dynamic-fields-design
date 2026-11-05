using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class ModuleTemplateRepository(DynamicDbContext db) : IModuleTemplateRepository
{
    public Task<List<ModuleTemplate>> ListAsync(CancellationToken ct) =>
        db.Set<ModuleTemplate>().AsNoTracking().OrderBy(t => t.Key).ToListAsync(ct);

    public Task<ModuleTemplate?> GetByKeyAsync(string key, CancellationToken ct) =>
        db.Set<ModuleTemplate>().AsNoTracking().FirstOrDefaultAsync(t => t.Key == key, ct);

    public void Add(ModuleTemplate t) => db.Set<ModuleTemplate>().Add(t);
}
