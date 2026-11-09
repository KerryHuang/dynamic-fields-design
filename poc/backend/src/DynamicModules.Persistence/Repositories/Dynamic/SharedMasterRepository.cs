using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class SharedMasterRepository(DynamicDbContext db) : ISharedMasterRepository
{
    public Task<List<AppMasterUse>> ListUsesAsync(CancellationToken ct) =>
        db.Set<AppMasterUse>().AsNoTracking().OrderBy(u => u.AppId).ThenBy(u => u.MasterAppId).ToListAsync(ct);

    public void AddUse(AppMasterUse use) => db.Set<AppMasterUse>().Add(use);

    public Task<List<AppGrant>> ListGrantsAsync(long moduleId, CancellationToken ct) =>
        db.Set<AppGrant>().Where(g => g.ModuleId == moduleId).OrderBy(g => g.Id).ToListAsync(ct);

    public void AddGrant(AppGrant grant) => db.Set<AppGrant>().Add(grant);

    public void RemoveGrant(AppGrant grant) => db.Set<AppGrant>().Remove(grant);

    public async Task ReplaceActionScopesAsync(long moduleId, IReadOnlyList<ModuleActionScope> rows, CancellationToken ct)
    {
        await db.Set<ModuleActionScope>().Where(s => s.ModuleId == moduleId).ExecuteDeleteAsync(ct);
        // ExecuteDelete 不經變更追蹤：先前追蹤中的同鍵列要放掉，否則新列會撞鍵
        foreach (var entry in db.ChangeTracker.Entries<ModuleActionScope>().Where(e => e.Entity.ModuleId == moduleId).ToList())
            entry.State = EntityState.Detached;
        db.Set<ModuleActionScope>().AddRange(rows);
    }

    public Task<List<ModuleActionScope>> ListActionScopesAsync(long moduleId, CancellationToken ct) =>
        db.Set<ModuleActionScope>().AsNoTracking().Where(s => s.ModuleId == moduleId).ToListAsync(ct);

    public Task<List<FieldGrant>> ListFieldGrantsAsync(IReadOnlyCollection<long> fieldIds, CancellationToken ct) =>
        db.Set<FieldGrant>().Where(g => fieldIds.Contains(g.FieldId)).OrderBy(g => g.Id).ToListAsync(ct);

    public void AddFieldGrant(FieldGrant grant) => db.Set<FieldGrant>().Add(grant);

    public void RemoveFieldGrant(FieldGrant grant) => db.Set<FieldGrant>().Remove(grant);

    public Task<List<AppOrphan>> ListOrphansAsync(string? formerAppKey, CancellationToken ct) =>
        db.Set<AppOrphan>().Where(o => formerAppKey == null || o.FormerAppKey == formerAppKey).OrderBy(o => o.Id).ToListAsync(ct);

    public void AddOrphan(AppOrphan orphan) => db.Set<AppOrphan>().Add(orphan);

    public void RemoveOrphan(AppOrphan orphan) => db.Set<AppOrphan>().Remove(orphan);

    public async Task<List<ContributedField>> ListContributedFieldsAsync(long moduleId, CancellationToken ct)
    {
        var rows = await (
            from f in db.Set<Field>().AsNoTracking()
            where f.ModuleId == moduleId
            join b in db.Set<AppField>().AsNoTracking() on f.Id equals b.FieldId
            where b.Name.Contains("/")
            join a in db.Set<App>().AsNoTracking() on b.AppId equals a.Id
            select new { f.Id, AppId = a.Id, a.Key, a.NameI18n, a.Status }).ToListAsync(ct);
        return rows.Select(r => new ContributedField(r.Id, r.AppId, r.Key, r.NameI18n, r.Status)).ToList();
    }

    public async Task<List<BlockOwner>> ListBlockOwnersAsync(long moduleId, CancellationToken ct)
    {
        var owned = await (
            from k in db.Set<Block>().AsNoTracking()
            where k.ModuleId == moduleId
            join b in db.Set<AppBlock>().AsNoTracking() on k.Id equals b.BlockId
            where b.Name.Contains("/")
            join a in db.Set<App>().AsNoTracking() on b.AppId equals a.Id
            select new BlockOwner(k.Id, a.Key)).ToListAsync(ct);
        var orphans = await (
            from k in db.Set<Block>().AsNoTracking()
            where k.ModuleId == moduleId
            join o in db.Set<AppOrphan>().AsNoTracking() on k.Id equals o.BlockId
            select new BlockOwner(k.Id, o.FormerAppKey)).ToListAsync(ct);
        return owned.Concat(orphans).ToList();
    }

    public async Task<ItemOwner> BlockOwnerAsync(long blockId, CancellationToken ct)
    {
        var contributor = await (from b in db.Set<AppBlock>().AsNoTracking()
                                 where b.BlockId == blockId && b.Name.Contains("/")
                                 join a in db.Set<App>().AsNoTracking() on b.AppId equals a.Id
                                 select new { a.Id, a.Key }).FirstOrDefaultAsync(ct);
        if (contributor is not null) return new ItemOwner(contributor.Id, contributor.Key, false);
        var orphan = await db.Set<AppOrphan>().AsNoTracking().Where(o => o.BlockId == blockId).Select(o => o.FormerAppKey).FirstOrDefaultAsync(ct);
        return orphan is not null ? new ItemOwner(null, orphan, true) : ItemOwner.Master;
    }

    public async Task<ItemOwner> FieldOwnerAsync(long fieldId, CancellationToken ct)
    {
        var bindings = await (from b in db.Set<AppField>().AsNoTracking()
                              where b.FieldId == fieldId
                              join a in db.Set<App>().AsNoTracking() on b.AppId equals a.Id
                              select new { a.Id, a.Key, a.Kind, b.Name }).ToListAsync(ct);
        if (bindings.FirstOrDefault(b => b.Name.Contains('/')) is { } contributor) return new ItemOwner(contributor.Id, contributor.Key, false);
        var orphan = await db.Set<AppOrphan>().AsNoTracking().Where(o => o.FieldId == fieldId).Select(o => o.FormerAppKey).FirstOrDefaultAsync(ct);
        if (orphan is not null) return new ItemOwner(null, orphan, true);
        return bindings.Any(b => b.Kind == AppKind.Master) ? ItemOwner.Master : ItemOwner.Master with { Known = false };
    }
}
