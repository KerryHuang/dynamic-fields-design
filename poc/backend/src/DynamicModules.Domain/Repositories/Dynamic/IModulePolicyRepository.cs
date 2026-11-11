using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>模組政策覆寫。</summary>
public interface IModulePolicyRepository
{
    /// <summary>不追蹤。</summary>
    Task<List<ModulePolicy>> ListByModuleAsync(long moduleId, CancellationToken ct);
    /// <summary>追蹤；沒有回 null。</summary>
    Task<ModulePolicy?> GetTrackedAsync(long moduleId, string code, CancellationToken ct);
    void Add(ModulePolicy policy);
    void Remove(ModulePolicy policy);
}
