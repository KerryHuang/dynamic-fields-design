using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface IRecordRepository
{
    /// <summary>排除已刪除的記錄。</summary>
    Task<Record?> GetAsync(long id, CancellationToken ct);
    /// <summary>模組全部記錄（含已刪除、追蹤中），供重算 Title；POC 量小全載，上線時要分批。</summary>
    Task<List<Record>> ListAllByModuleAsync(long moduleId, CancellationToken ct);
    /// <summary>模組是否有任何記錄，含已刪除的。</summary>
    Task<bool> AnyByModuleAsync(long moduleId, CancellationToken ct);
    /// <summary>模組內未刪除記錄的筆數。</summary>
    Task<int> CountByModuleAsync(long moduleId, CancellationToken ct);
    /// <summary>擴充模組中、host 鍵為 hostKey 的未刪除記錄。</summary>
    Task<Record?> GetByHostAsync(long moduleId, string hostKey, CancellationToken ct);
    void Add(Record r);
    /// <summary>報表用：未刪除、在 ownerUserIds 範圍內（null 不限）的記錄，依 Id 遞增、不追蹤、不分頁，最多 limit 筆。</summary>
    Task<IReadOnlyList<Record>> ListForReportAsync(long moduleId, IReadOnlyCollection<string>? ownerUserIds, int limit, CancellationToken ct);
    /// <summary>ids 中屬於該模組且未刪除的記錄（不追蹤、不看資料範圍），供報表帶出參照目標的欄位。</summary>
    Task<IReadOnlyList<Record>> ListByIdsAsync(long moduleId, IReadOnlyCollection<long> ids, CancellationToken ct);
    /// <summary>ids 中屬於該模組、未刪除、且在 ownerUserIds 範圍內（null 不限）的記錄 Id。</summary>
    Task<IReadOnlySet<long>> ListVisibleIdsAsync(long moduleId, IReadOnlyCollection<long> ids, IReadOnlyCollection<string>? ownerUserIds, CancellationToken ct);
    /// <summary>ids 中屬於該模組且未刪除的記錄（不看資料範圍），供判斷參照是 missing 還是 restricted。</summary>
    Task<IReadOnlyList<RecordStub>> ListExistingAsync(long moduleId, IReadOnlyCollection<long> ids, CancellationToken ct);
    /// <summary>未刪除、在 ownerUserIds 範圍內（null 不限）、Title 含 keyword（null 不限）的記錄，依 Title 排序分頁。</summary>
    Task<(IReadOnlyList<RecordStub> items, int total)> QueryLightAsync(
        long moduleId, string? keyword, IReadOnlyCollection<string>? ownerUserIds, int page, int size, CancellationToken ct);
    /// <summary>依 RecordQuery 篩選、排序、分頁（不追蹤），排除已刪除的記錄；最後一律以 Id 遞減定序。</summary>
    Task<(List<Record> items, int total)> QueryAsync(RecordQuery q, CancellationToken ct);
    /// <summary>參照欄位 fieldId 的值等於 refKey、未刪除、在 ownerUserIds 範圍內（null 不限）的記錄，依 Id 遞減分頁（不追蹤）。</summary>
    Task<(IReadOnlyList<Record> Items, int Total)> QueryByRefAsync(
        long fieldId, string refKey, IReadOnlyCollection<string>? ownerUserIds, int page, int size, CancellationToken ct);
    /// <summary>參照欄位 fieldId 的值等於 refKey、未刪除的記錄（追蹤中、不套資料範圍、不分頁），供刪除規劃。</summary>
    Task<IReadOnlyList<Record>> ListByRefAsync(long fieldId, string refKey, CancellationToken ct);
    /// <summary>明細欄位 detailFieldId 指向 parentKey 的明細列 Id（含已刪除：軟刪除不清索引），供讀歷程。</summary>
    Task<IReadOnlyList<long>> ListDetailRowIdsAsync(long detailFieldId, string parentKey, CancellationToken ct);
    /// <summary>刪除某筆記錄在某欄位的索引列（欄位值被清空時用）。</summary>
    Task RemoveFieldIndexAsync(long recordId, long fieldId, CancellationToken ct);
    /// <summary>新記錄（Id == 0）設定 record.Index；既有記錄先刪舊列再加新列。</summary>
    Task ReplaceIndexAsync(Record record, IReadOnlyList<RecordIndex> rows, CancellationToken ct);
    /// <summary>刪除某欄位的全部索引列，再加入 rows（rows 的 RecordId 須已填）。</summary>
    Task ReplaceFieldIndexAsync(long fieldId, IReadOnlyList<RecordIndex> rows, CancellationToken ct);
    /// <summary>未刪除記錄中 Id 大於 afterId 的前 take 筆（依 Id 遞增，追蹤中），供分批回填索引。</summary>
    Task<List<Record>> ListBatchByModuleAsync(long moduleId, long afterId, int take, CancellationToken ct);
    /// <summary>刪除某欄位的全部索引列（一句 DELETE），並讓 change tracker 忘記已追蹤的舊列；分批回填的第一步。</summary>
    Task ClearFieldIndexAsync(long fieldId, CancellationToken ct);
    void AddIndexRows(IReadOnlyList<RecordIndex> rows);
    /// <summary>分批回填存檔後，讓 change tracker 忘記這批記錄與它們的索引列，記憶體才不隨批次累積。</summary>
    void ReleaseTracked(IReadOnlyList<Record> batch);
    /// <summary>該欄位是否有「有數值卻沒有 ScaledValue」的索引列（升級前建立的索引）。</summary>
    Task<bool> HasUnscaledIndexAsync(long fieldId, CancellationToken ct);
    /// <summary>
    /// 跨記錄驗證的聚合（設計 §5.2）：同一模組內未刪除、符合組鍵與條件的記錄。SQLite 整數加總溢位時丟
    /// <see cref="AggregateOverflowException"/>。呼叫端要先把變更存進資料庫（同一個交易內）。
    /// </summary>
    Task<AggregateResult> AggregateAsync(AggregateQuery query, CancellationToken ct);
}

public sealed class AggregateOverflowException(Exception inner) : Exception("加總超出可計算範圍", inner);

public record RecordStub(long Id, string Title, string OwnerUserId);
