using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface IAppRepository
{
    /// <summary>含成員模組對照；追蹤中（開關要改狀態）。</summary>
    Task<List<App>> ListAsync(CancellationToken ct);
    Task<App?> GetByKeyAsync(string key, CancellationToken ct);
    /// <summary>模組所屬的應用程式（含成員）；不屬於任何應用程式回 null。</summary>
    Task<App?> FindByModuleIdAsync(long moduleId, CancellationToken ct);
    void Add(App app);
    void Remove(App app);
}

public interface IAppPackageRepository
{
    Task<List<AppPackage>> ListAsync(CancellationToken ct);
    Task<AppPackage?> GetByKeyAsync(string key, CancellationToken ct);
    void Add(AppPackage p);
}
