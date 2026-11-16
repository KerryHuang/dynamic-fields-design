using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class ConversionRepository(DynamicDbContext db) : IConversionRepository
{
    public Task<Conversion?> GetByKeyAsync(string key, CancellationToken ct) =>
        db.Set<Conversion>().FirstOrDefaultAsync(c => c.Key == key, ct);

    public Task<List<Conversion>> ListAsync(CancellationToken ct) =>
        db.Set<Conversion>().AsNoTracking().OrderBy(c => c.Key).ToListAsync(ct);

    public Task<List<Conversion>> ListBySourceAsync(long sourceModuleId, CancellationToken ct) =>
        db.Set<Conversion>().AsNoTracking().Where(c => c.SourceModuleId == sourceModuleId).OrderBy(c => c.Key).ToListAsync(ct);

    public Task<List<Conversion>> ListTouchingAsync(long moduleId, CancellationToken ct) =>
        db.Set<Conversion>().Where(c => c.SourceModuleId == moduleId || c.TargetModuleId == moduleId).ToListAsync(ct);

    public void Add(Conversion c) => db.Set<Conversion>().Add(c);

    public void Remove(Conversion c) => db.Set<Conversion>().Remove(c);
}
