using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>安全刪除專用：引用盤點需要的全域讀取，以及永久清除資料的批次刪除。清除必須在呼叫端的交易內執行。</summary>
public interface IDeletionRepository
{
    /// <summary>所有模組（含區塊、欄位、選項、規則），不追蹤。</summary>
    Task<List<Module>> ListModulesAsync(CancellationToken ct);
    Task<List<ListView>> ListViewsAsync(CancellationToken ct);
    Task<List<Report>> ListReportsAsync(CancellationToken ct);

    /// <summary>模組內未軟刪除（deleted = false）或已軟刪除（true）的記錄數。</summary>
    Task<int> CountRecordsAsync(long moduleId, bool deleted, CancellationToken ct);
    /// <summary>模組全部記錄（含軟刪除）的索引列數與歷程列數。</summary>
    Task<int> CountIndexRowsByModuleAsync(long moduleId, CancellationToken ct);
    Task<int> CountHistoryRowsByModuleAsync(long moduleId, CancellationToken ct);
    Task<int> CountIndexRowsByFieldsAsync(IReadOnlyCollection<long> fieldIds, CancellationToken ct);
    Task<int> CountHistoryRowsByFieldsAsync(IReadOnlyCollection<long> fieldIds, CancellationToken ct);

    /// <summary>未軟刪除的記錄中，各欄位鍵有非空值的記錄數；沒有值的鍵不會出現在結果。</summary>
    Task<IReadOnlyDictionary<string, int>> CountLiveRecordsWithValueAsync(long moduleId, IReadOnlyCollection<string> fieldKeys, CancellationToken ct);
    /// <summary>ValuesJson 含任一欄位鍵的記錄（含軟刪除，追蹤中，供改寫）。</summary>
    Task<List<Record>> ListRecordsWithKeysAsync(long moduleId, IReadOnlyCollection<string> fieldKeys, CancellationToken ct);

    /// <summary>永久清除模組的記錄、索引、歷程，以及不隨模組級聯刪除的權限與流水號等設定列。</summary>
    Task PurgeModuleAsync(Module module, CancellationToken ct);
    /// <summary>永久清除欄位的索引、歷程，以及欄位權限、選項範圍與流水號列。</summary>
    Task PurgeFieldsAsync(IReadOnlyCollection<Field> fields, CancellationToken ct);
}
