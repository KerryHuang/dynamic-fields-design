using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface IModuleRepository
{
    Task<Module?> GetByKeyAsync(string key, bool includeChildren, CancellationToken ct);
    Task<Module?> GetByIdAsync(long id, bool includeChildren, CancellationToken ct);
    Task<bool> KeyExistsAsync(string key, CancellationToken ct);
    /// <summary>以下三個依子層 Id 找出所屬模組（含全部子層，供 TouchVersion 與同模組檢查）。</summary>
    Task<Module?> GetByBlockIdAsync(long blockId, CancellationToken ct);
    Task<Module?> GetByFieldIdAsync(long fieldId, CancellationToken ct);
    Task<Module?> GetByOptionIdAsync(long optionId, CancellationToken ct);
    Task<HashSet<string>> ListUserIdsAsync(CancellationToken ct);
    /// <summary>所有模組（含未啟用）中、啟用中且指向該模組的參照欄位；來源模組只載入區塊與欄位。呼叫端自行過濾來源模組狀態。</summary>
    Task<IReadOnlyList<(Module Source, Field Field)>> ListIncomingReferenceFieldsAsync(string targetModuleKey, CancellationToken ct);
    /// <summary>是否有任何參照欄位（含停用的欄位、任何模組、含自己）指向該模組。</summary>
    Task<bool> IsReferencedAsync(string targetModuleKey, CancellationToken ct);
    /// <summary>以 parentKey 為表頭的明細模組及其明細欄位（啟用中）；子模組只載入區塊與欄位。</summary>
    Task<IReadOnlyList<(Module Child, Field ParentField)>> ListDetailModulesAsync(string parentKey, CancellationToken ct);
    /// <summary>所有明細模組鍵對應的表頭模組鍵，供模組清單標示。</summary>
    Task<IReadOnlyDictionary<string, string>> ListDetailParentKeysAsync(CancellationToken ct);
    /// <summary>來源參照欄位屬於 sourceModuleId 的啟用中彙總欄位；Parent 是彙總欄位所在模組，Source 是來源參照欄位。</summary>
    Task<IReadOnlyList<(Module Parent, Field Rollup, Field Source)>> ListRollupsBySourceModuleAsync(long sourceModuleId, CancellationToken ct);
    /// <summary>該既有實體是否已有擴充模組。</summary>
    Task<bool> HostEntityExistsAsync(string hostEntityKey, CancellationToken ct);
    void Add(Module m);
    /// <summary>移除模組；區塊、欄位、選項、規則由級聯刪除，需先載入子層（includeChildren）。</summary>
    void Remove(Module m);
    Task<List<Module>> ListAsync(CancellationToken ct);
    Task<List<Module>> ListActiveAsync(CancellationToken ct);
}
