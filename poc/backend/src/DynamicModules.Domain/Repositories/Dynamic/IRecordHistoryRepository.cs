using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface IRecordHistoryRepository
{
    void Add(RecordHistory h);
    /// <summary>
    /// 指定記錄的歷程，依 ChangedTime 遞減、再依 Id 遞減分頁（不追蹤）；page 從 1 開始。
    /// FieldId 在 hiddenFieldIds 裡的列在分頁前排除，總數才不會洩漏看不到的欄位。
    /// </summary>
    Task<(IReadOnlyList<RecordHistory> Items, int Total)> QueryAsync(
        IReadOnlyCollection<long> recordIds, IReadOnlyCollection<long> hiddenFieldIds, int page, int size, CancellationToken ct);
}
