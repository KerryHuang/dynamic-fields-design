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
}

public class AppPackageRepository(DynamicDbContext db) : IAppPackageRepository
{
    public Task<List<AppPackage>> ListAsync(CancellationToken ct) =>
        db.Set<AppPackage>().AsNoTracking().OrderBy(p => p.Key).ToListAsync(ct);

    public Task<AppPackage?> GetByKeyAsync(string key, CancellationToken ct) =>
        db.Set<AppPackage>().AsNoTracking().FirstOrDefaultAsync(p => p.Key == key, ct);

    public void Add(AppPackage p) => db.Set<AppPackage>().Add(p);
}
