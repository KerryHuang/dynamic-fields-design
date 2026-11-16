using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface IModuleTemplateRepository
{
    Task<List<ModuleTemplate>> ListAsync(CancellationToken ct);
    Task<ModuleTemplate?> GetByKeyAsync(string key, CancellationToken ct);
    void Add(ModuleTemplate t);
}
