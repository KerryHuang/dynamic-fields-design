using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class PrintTemplateRepository(DynamicDbContext db) : IPrintTemplateRepository
{
    public Task<PrintTemplate?> GetAsync(long moduleId, string key, CancellationToken ct) =>
        db.Set<PrintTemplate>().FirstOrDefaultAsync(t => t.ModuleId == moduleId && t.Key == key, ct);

    public Task<List<PrintTemplate>> ListByModuleAsync(long moduleId, CancellationToken ct) =>
        db.Set<PrintTemplate>().Where(t => t.ModuleId == moduleId).OrderBy(t => t.Key).ToListAsync(ct);

    public void Add(PrintTemplate t) => db.Set<PrintTemplate>().Add(t);

    public void Remove(PrintTemplate t) => db.Set<PrintTemplate>().Remove(t);
}
