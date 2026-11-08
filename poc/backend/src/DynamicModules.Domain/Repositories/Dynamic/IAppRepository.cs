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

    /// <summary>開啟時的代號對照（不追蹤）。</summary>
    Task<AppBindings> GetBindingsAsync(long appId, CancellationToken ct);
    void AddBindings(IEnumerable<AppField> fields, IEnumerable<AppOption> options, IEnumerable<AppBlock> blocks);
    /// <summary>全部整合（追蹤中，狀態要改）。</summary>
    Task<List<AppIntegration>> ListIntegrationsAsync(CancellationToken ct);
    void AddIntegration(AppIntegration integration);
}

/// <summary>Name 是 &lt;模組代號&gt;.&lt;欄位／選項／區塊代號&gt;。</summary>
public record AppBindings(
    IReadOnlyDictionary<string, long> Fields, IReadOnlyDictionary<string, long> Options, IReadOnlyDictionary<string, long> Blocks);

public interface IAppPackageRepository
{
    Task<List<AppPackage>> ListAsync(CancellationToken ct);
    Task<AppPackage?> GetByKeyAsync(string key, CancellationToken ct);
    void Add(AppPackage p);
}
