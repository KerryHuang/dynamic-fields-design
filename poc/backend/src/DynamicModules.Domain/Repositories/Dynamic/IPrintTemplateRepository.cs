using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface IPrintTemplateRepository
{
    Task<PrintTemplate?> GetAsync(long moduleId, string key, CancellationToken ct);
    /// <summary>該模組的所有範本（含停用），依 Key 排序；有追蹤。</summary>
    Task<List<PrintTemplate>> ListByModuleAsync(long moduleId, CancellationToken ct);
    void Add(PrintTemplate t);
    void Remove(PrintTemplate t);
}
