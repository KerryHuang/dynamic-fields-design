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

public interface IEventFactRepository
{
    Task<EventFact?> FindAsync(string eventKey, string effectCode, long sourceRecordId, long itemKey, CancellationToken ct);
    /// <summary>某事件的某效果為這筆來源記錄新增（或沖銷）的全部事實，依 Id 遞增，不追蹤。</summary>
    Task<List<EventFact>> ListByEffectAsync(string eventKey, string effectCode, long sourceRecordId, CancellationToken ct);
    /// <summary>這筆記錄作為來源的全部事實（含其他請求產生的），依 Id 遞增，不追蹤。</summary>
    Task<List<EventFact>> ListBySourceAsync(long sourceRecordId, CancellationToken ct);
    /// <summary>這筆記錄是否已有事實（事實鎖，每次都查）。</summary>
    Task<bool> AnyBySourceAsync(long sourceRecordId, CancellationToken ct);
    /// <summary>ids 之中，在 operationId 以外的請求就已經有事實的記錄 Id（寫入後盾用）。</summary>
    Task<HashSet<long>> ListFrozenBeforeAsync(IReadOnlyCollection<long> sourceRecordIds, string operationId, CancellationToken ct);
    /// <summary>事實的目標記錄 Id 為 targetRecordId 的事實（連動結果的反查）。</summary>
    Task<List<EventFact>> ListByTargetAsync(long targetRecordId, CancellationToken ct);
    void Add(EventFact fact);
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
    /// <summary>該模組是否設定為分錄（不載入實體，寫入路徑每次都問）。</summary>
    Task<bool> IsLedgerModuleAsync(long moduleId, CancellationToken ct);
    Task<LedgerReversal?> FindReversalOfAsync(long originalRecordId, CancellationToken ct);
    Task<LedgerReversal?> FindReversalByAsync(long reversalRecordId, CancellationToken ct);
    void AddReversal(LedgerReversal r);
    void Add(ModuleLedger l);
    void Remove(ModuleLedger l);
}

public interface IOperationRepository
{
    Task<OperationRecord?> GetAsync(string userId, string key, CancellationToken ct);
    void Add(OperationRecord record);
    /// <summary>刪除建立時間早於 cutoff 的紀錄（單句 DELETE，CreatedTime 有索引）。</summary>
    Task PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct);
}
