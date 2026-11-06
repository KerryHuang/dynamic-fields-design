using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class ModuleRepository(DynamicDbContext db) : IModuleRepository
{
    public void RemoveWorkflow(Workflow w) => db.Set<Workflow>().Remove(w);

    public Task<Module?> GetByKeyAsync(string key, bool includeChildren, CancellationToken ct)
    {
        IQueryable<Module> query = db.Set<Module>();
        if (includeChildren)
            query = query.Include(m => m.Blocks).ThenInclude(b => b.Fields).ThenInclude(f => f.Options)
                .Include(m => m.Rules.OrderBy(r => r.Sequence)).Include(m => m.Workflow);
        return query.FirstOrDefaultAsync(m => m.Key == key, ct);
    }

    public Task<Module?> GetByIdAsync(long id, bool includeChildren, CancellationToken ct)
    {
        IQueryable<Module> query = db.Set<Module>();
        if (includeChildren)
            query = query.Include(m => m.Blocks).ThenInclude(b => b.Fields).ThenInclude(f => f.Options)
                .Include(m => m.Rules.OrderBy(r => r.Sequence)).Include(m => m.Workflow);
        return query.FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    private IQueryable<Module> WithChildren() =>
        db.Set<Module>().Include(m => m.Blocks).ThenInclude(b => b.Fields).ThenInclude(f => f.Options)
            .Include(m => m.Rules.OrderBy(r => r.Sequence)).Include(m => m.Workflow);

    public Task<Module?> GetByBlockIdAsync(long blockId, CancellationToken ct) =>
        WithChildren().FirstOrDefaultAsync(m => m.Blocks.Any(b => b.Id == blockId), ct);

    public Task<Module?> GetByFieldIdAsync(long fieldId, CancellationToken ct) =>
        WithChildren().FirstOrDefaultAsync(m => m.Blocks.Any(b => b.Fields.Any(f => f.Id == fieldId)), ct);

    public Task<Module?> GetByOptionIdAsync(long optionId, CancellationToken ct) =>
        WithChildren().FirstOrDefaultAsync(
            m => m.Blocks.Any(b => b.Fields.Any(f => f.Options.Any(o => o.Id == optionId))), ct);

    public async Task<HashSet<string>> ListUserIdsAsync(CancellationToken ct) =>
        (await db.Set<AppUser>().AsNoTracking().Select(u => u.UserId).ToListAsync(ct)).ToHashSet();

    public Task<bool> KeyExistsAsync(string key, CancellationToken ct) =>
        db.Set<Module>().AnyAsync(m => m.Key == key, ct);

    public Task<bool> HostEntityExistsAsync(string hostEntityKey, CancellationToken ct) =>
        db.Set<Module>().AnyAsync(m => m.HostEntityKey == hostEntityKey, ct);

    public async Task<IReadOnlyList<(Module Source, Field Field)>> ListIncomingReferenceFieldsAsync(string targetModuleKey, CancellationToken ct)
    {
        var modules = await db.Set<Module>().AsNoTracking().Include(m => m.Blocks).ThenInclude(b => b.Fields)
            .OrderBy(m => m.Id).ToListAsync(ct);
        return modules
            .SelectMany(m => m.Blocks.SelectMany(b => b.Fields).Select(f => (Source: m, Field: f)))
            .Where(x => x.Field is { Type: FieldType.Reference, Status: FieldStatus.Active }
                        && x.Field.Config.RefTarget is { Kind: ReferenceTarget.ModuleKind } t && t.Key == targetModuleKey)
            .OrderBy(x => x.Field.Id).ToList();
    }

    public async Task<bool> IsReferencedAsync(string targetModuleKey, CancellationToken ct)
    {
        var fields = await db.Set<Field>().AsNoTracking().Where(f => f.Type == FieldType.Reference).ToListAsync(ct);
        return fields.Any(f => f.Config.RefTarget is { Kind: ReferenceTarget.ModuleKind } t && t.Key == targetModuleKey);
    }

    public async Task<IReadOnlyList<(Module Child, Field ParentField)>> ListDetailModulesAsync(string parentKey, CancellationToken ct) =>
        (await ListIncomingReferenceFieldsAsync(parentKey, ct))
        .Where(x => x.Field.Config.Relation == ReferenceRelation.Detail)
        .Select(x => (x.Source, x.Field)).ToList();

    public async Task<IReadOnlyDictionary<string, string>> ListDetailParentKeysAsync(CancellationToken ct)
    {
        var modules = await db.Set<Module>().AsNoTracking().Include(m => m.Blocks).ThenInclude(b => b.Fields).ToListAsync(ct);
        return modules.Select(m => (m.Key, Parent: ModuleRelations.ParentKeyOf(m)))
            .Where(x => x.Parent is not null).ToDictionary(x => x.Key, x => x.Parent!);
    }

    public async Task<IReadOnlyList<(Module Parent, Field Rollup, Field Source)>> ListRollupsBySourceModuleAsync(
        long sourceModuleId, CancellationToken ct)
    {
        var modules = await db.Set<Module>().AsNoTracking().Include(m => m.Blocks).ThenInclude(b => b.Fields)
            .OrderBy(m => m.Id).ToListAsync(ct);
        var sourceFields = modules.Where(m => m.Id == sourceModuleId)
            .SelectMany(m => m.Blocks.SelectMany(b => b.Fields)).ToDictionary(f => f.Id);
        return modules
            .SelectMany(m => m.Blocks.SelectMany(b => b.Fields).Select(f => (Parent: m, Rollup: f)))
            .Where(x => x.Rollup is { Type: FieldType.Rollup, Status: FieldStatus.Active, Config.Rollup: not null }
                        && sourceFields.ContainsKey(x.Rollup.Config.Rollup!.SourceFieldId))
            .OrderBy(x => x.Rollup.Id)
            .Select(x => (x.Parent, x.Rollup, sourceFields[x.Rollup.Config.Rollup!.SourceFieldId])).ToList();
    }

    public void Add(Module m) => db.Set<Module>().Add(m);

    public void Remove(Module m) => db.Set<Module>().Remove(m);

    public Task<List<Module>> ListAsync(CancellationToken ct) =>
        db.Set<Module>().AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct);

    public Task<List<Module>> ListActiveAsync(CancellationToken ct) =>
        db.Set<Module>().AsNoTracking().Include(m => m.Blocks).ThenInclude(b => b.Fields)
            .Where(m => m.Status == ModuleStatus.Active).OrderBy(m => m.Id).ToListAsync(ct);
}
