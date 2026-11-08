using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class AppRepository(DynamicDbContext db) : IAppRepository
{
    public Task<List<App>> ListAsync(CancellationToken ct) =>
        db.Set<App>().Include(a => a.Modules).Include(a => a.Reports).OrderBy(a => a.Key).ToListAsync(ct);

    public Task<App?> GetByKeyAsync(string key, CancellationToken ct) =>
        db.Set<App>().Include(a => a.Modules).Include(a => a.Reports).FirstOrDefaultAsync(a => a.Key == key, ct);

    public async Task<App?> FindByModuleIdAsync(long moduleId, CancellationToken ct)
    {
        var appId = await db.Set<AppModule>().Where(m => m.ModuleId == moduleId).Select(m => (long?)m.AppId).FirstOrDefaultAsync(ct);
        return appId is null ? null : await db.Set<App>().Include(a => a.Modules).Include(a => a.Reports).FirstAsync(a => a.Id == appId, ct);
    }

    public void Add(App app) => db.Set<App>().Add(app);

    public void Remove(App app) => db.Set<App>().Remove(app);

    public async Task<AppBindings> GetBindingsAsync(long appId, CancellationToken ct) => new(
        await db.Set<AppField>().AsNoTracking().Where(x => x.AppId == appId).ToDictionaryAsync(x => x.Name, x => x.FieldId, ct),
        await db.Set<AppOption>().AsNoTracking().Where(x => x.AppId == appId).ToDictionaryAsync(x => x.Name, x => x.OptionId, ct),
        await db.Set<AppBlock>().AsNoTracking().Where(x => x.AppId == appId).ToDictionaryAsync(x => x.Name, x => x.BlockId, ct));

    public void AddBindings(IEnumerable<AppField> fields, IEnumerable<AppOption> options, IEnumerable<AppBlock> blocks)
    {
        db.Set<AppField>().AddRange(fields);
        db.Set<AppOption>().AddRange(options);
        db.Set<AppBlock>().AddRange(blocks);
    }

    public Task<List<AppIntegration>> ListIntegrationsAsync(CancellationToken ct) =>
        db.Set<AppIntegration>().OrderBy(x => x.AppId).ThenBy(x => x.Code).ToListAsync(ct);

    public void AddIntegration(AppIntegration integration) => db.Set<AppIntegration>().Add(integration);
}

public class AppPackageRepository(DynamicDbContext db) : IAppPackageRepository
{
    public Task<List<AppPackage>> ListAsync(CancellationToken ct) =>
        db.Set<AppPackage>().AsNoTracking().OrderBy(p => p.Key).ToListAsync(ct);

    public Task<AppPackage?> GetByKeyAsync(string key, CancellationToken ct) =>
        db.Set<AppPackage>().AsNoTracking().FirstOrDefaultAsync(p => p.Key == key, ct);

    public void Add(AppPackage p) => db.Set<AppPackage>().Add(p);
}
