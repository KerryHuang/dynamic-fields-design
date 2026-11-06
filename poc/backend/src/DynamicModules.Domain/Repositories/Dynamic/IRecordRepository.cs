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
}

public record RecordStub(long Id, string Title, string OwnerUserId);
