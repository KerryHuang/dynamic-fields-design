using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface IEventRepository
{
    Task<ModuleEvent?> GetByKeyAsync(string key, CancellationToken ct);
    /// <summary>全部事件（含停用的），依 Key 排序，不追蹤。</summary>
    Task<List<ModuleEvent>> ListAsync(CancellationToken ct);
    /// <summary>以該模組為來源的事件（含停用的），依 Key 排序，不追蹤。</summary>
    Task<List<ModuleEvent>> ListBySourceAsync(long sourceModuleId, CancellationToken ct);
    /// <summary>以該模組為來源的啟用事件，不追蹤。</summary>
    Task<List<ModuleEvent>> ListActiveBySourceAsync(long sourceModuleId, CancellationToken ct);
    void Add(ModuleEvent e);
    void Remove(ModuleEvent e);
}

public interface ICheckRepository
{
    Task<ModuleCheck?> GetByKeyAsync(string key, CancellationToken ct);
    Task<List<ModuleCheck>> ListAsync(CancellationToken ct);
    Task<List<ModuleCheck>> ListByModuleAsync(long moduleId, CancellationToken ct);
    Task<List<ModuleCheck>> ListActiveByModuleAsync(long moduleId, CancellationToken ct);
    void Add(ModuleCheck c);
    void Remove(ModuleCheck c);
}

public interface ILedgerRepository
{
    /// <summary>該模組的分錄設定，有追蹤。</summary>
    Task<ModuleLedger?> GetByModuleAsync(long moduleId, CancellationToken ct);
    Task<List<ModuleLedger>> ListAsync(CancellationToken ct);
    void Add(ModuleLedger l);
    void Remove(ModuleLedger l);
}
