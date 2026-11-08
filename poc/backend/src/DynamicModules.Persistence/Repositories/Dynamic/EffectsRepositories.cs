using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class EventFactRepository(DynamicDbContext db) : IEventFactRepository
{
    public Task<EventFact?> FindAsync(string eventKey, string effectCode, long sourceRecordId, long itemKey, CancellationToken ct) =>
        db.Set<EventFact>().AsNoTracking().FirstOrDefaultAsync(
            f => f.EventKey == eventKey && f.EffectCode == effectCode && f.SourceRecordId == sourceRecordId && f.ItemKey == itemKey, ct);

    public Task<List<EventFact>> ListByEffectAsync(string eventKey, string effectCode, long sourceRecordId, CancellationToken ct) =>
        db.Set<EventFact>().AsNoTracking()
            .Where(f => f.EventKey == eventKey && f.EffectCode == effectCode && f.SourceRecordId == sourceRecordId).OrderBy(f => f.Id).ToListAsync(ct);

    public Task<List<EventFact>> ListBySourceAsync(long sourceRecordId, CancellationToken ct) =>
        db.Set<EventFact>().AsNoTracking().Where(f => f.SourceRecordId == sourceRecordId).OrderBy(f => f.Id).ToListAsync(ct);

    public Task<bool> AnyBySourceAsync(long sourceRecordId, CancellationToken ct) =>
        db.Set<EventFact>().AsNoTracking().AnyAsync(f => f.SourceRecordId == sourceRecordId, ct);

    public async Task<HashSet<long>> ListFrozenBeforeAsync(IReadOnlyCollection<long> sourceRecordIds, string operationId, CancellationToken ct) =>
        (await db.Set<EventFact>().AsNoTracking()
            .Where(f => sourceRecordIds.Contains(f.SourceRecordId) && f.OperationId != operationId)
            .Select(f => f.SourceRecordId).Distinct().ToListAsync(ct)).ToHashSet();

    public Task<List<EventFact>> ListByTargetAsync(long targetRecordId, CancellationToken ct) =>
        db.Set<EventFact>().AsNoTracking().Where(f => f.TargetRecordId == targetRecordId).OrderBy(f => f.Id).ToListAsync(ct);

    public void Add(EventFact fact) => db.Set<EventFact>().Add(fact);
}

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

    public Task<bool> IsLedgerModuleAsync(long moduleId, CancellationToken ct) =>
        db.Set<ModuleLedger>().AnyAsync(l => l.ModuleId == moduleId, ct);

    public Task<LedgerReversal?> FindReversalOfAsync(long originalRecordId, CancellationToken ct) =>
        db.Set<LedgerReversal>().AsNoTracking().FirstOrDefaultAsync(r => r.OriginalRecordId == originalRecordId, ct);

    public Task<LedgerReversal?> FindReversalByAsync(long reversalRecordId, CancellationToken ct) =>
        db.Set<LedgerReversal>().AsNoTracking().FirstOrDefaultAsync(r => r.ReversalRecordId == reversalRecordId, ct);

    public void AddReversal(LedgerReversal r) => db.Set<LedgerReversal>().Add(r);

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
