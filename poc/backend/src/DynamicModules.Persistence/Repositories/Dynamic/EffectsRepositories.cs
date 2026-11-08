using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class EventRepository(DynamicDbContext db) : IEventRepository
{
    public Task<ModuleEvent?> GetByKeyAsync(string key, CancellationToken ct) =>
        db.Set<ModuleEvent>().FirstOrDefaultAsync(e => e.Key == key, ct);

    public Task<List<ModuleEvent>> ListAsync(CancellationToken ct) =>
        db.Set<ModuleEvent>().AsNoTracking().OrderBy(e => e.Key).ToListAsync(ct);

    public Task<List<ModuleEvent>> ListBySourceAsync(long sourceModuleId, CancellationToken ct) =>
        db.Set<ModuleEvent>().AsNoTracking().Where(e => e.SourceModuleId == sourceModuleId).OrderBy(e => e.Key).ToListAsync(ct);

    public Task<List<ModuleEvent>> ListActiveBySourceAsync(long sourceModuleId, CancellationToken ct) =>
        db.Set<ModuleEvent>().AsNoTracking().Where(e => e.SourceModuleId == sourceModuleId && e.Active).OrderBy(e => e.Key).ToListAsync(ct);

    public void Add(ModuleEvent e) => db.Set<ModuleEvent>().Add(e);

    public void Remove(ModuleEvent e) => db.Set<ModuleEvent>().Remove(e);
}

public class CheckRepository(DynamicDbContext db) : ICheckRepository
{
    public Task<ModuleCheck?> GetByKeyAsync(string key, CancellationToken ct) =>
        db.Set<ModuleCheck>().FirstOrDefaultAsync(c => c.Key == key, ct);

    public Task<List<ModuleCheck>> ListAsync(CancellationToken ct) =>
        db.Set<ModuleCheck>().AsNoTracking().OrderBy(c => c.Key).ToListAsync(ct);

    public Task<List<ModuleCheck>> ListByModuleAsync(long moduleId, CancellationToken ct) =>
        db.Set<ModuleCheck>().AsNoTracking().Where(c => c.ModuleId == moduleId).OrderBy(c => c.Key).ToListAsync(ct);

    public Task<List<ModuleCheck>> ListActiveByModuleAsync(long moduleId, CancellationToken ct) =>
        db.Set<ModuleCheck>().AsNoTracking().Where(c => c.ModuleId == moduleId && c.Active).OrderBy(c => c.Key).ToListAsync(ct);

    public void Add(ModuleCheck c) => db.Set<ModuleCheck>().Add(c);

    public void Remove(ModuleCheck c) => db.Set<ModuleCheck>().Remove(c);
}

public class LedgerRepository(DynamicDbContext db) : ILedgerRepository
{
    public Task<ModuleLedger?> GetByModuleAsync(long moduleId, CancellationToken ct) =>
        db.Set<ModuleLedger>().FirstOrDefaultAsync(l => l.ModuleId == moduleId, ct);

    public Task<List<ModuleLedger>> ListAsync(CancellationToken ct) =>
        db.Set<ModuleLedger>().AsNoTracking().ToListAsync(ct);

    public void Add(ModuleLedger l) => db.Set<ModuleLedger>().Add(l);

    public void Remove(ModuleLedger l) => db.Set<ModuleLedger>().Remove(l);
}

public class OperationRepository(DynamicDbContext db) : IOperationRepository
{
    public Task<OperationRecord?> GetAsync(string userId, string key, CancellationToken ct) =>
        db.Set<OperationRecord>().AsNoTracking().FirstOrDefaultAsync(o => o.UserId == userId && o.Key == key, ct);

    public void Add(OperationRecord record) => db.Set<OperationRecord>().Add(record);

    public Task PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct) =>
        db.Set<OperationRecord>().Where(o => o.CreatedTime < cutoff).ExecuteDeleteAsync(ct);
}
