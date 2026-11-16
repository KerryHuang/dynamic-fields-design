using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface IConversionRepository
{
    Task<Conversion?> GetByKeyAsync(string key, CancellationToken ct);
    Task<List<Conversion>> ListAsync(CancellationToken ct);
    /// <summary>以該模組為來源的轉換（含停用的），依 Key 排序。</summary>
    Task<List<Conversion>> ListBySourceAsync(long sourceModuleId, CancellationToken ct);
    /// <summary>以該模組為來源或目標的轉換（含停用的），有追蹤，可直接 Remove。</summary>
    Task<List<Conversion>> ListTouchingAsync(long moduleId, CancellationToken ct);
    void Add(Conversion c);
    void Remove(Conversion c);
}
