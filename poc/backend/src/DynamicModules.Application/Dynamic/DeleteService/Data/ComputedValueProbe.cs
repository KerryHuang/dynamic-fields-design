using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.DeleteService.Data;

/// <summary>
/// 公式與彙總沒有歷程也沒有索引列，刪除它們時 Purge 全為 0，但未刪除記錄上可能有值會被清掉。
/// 受保護記錄的守衛（簽核中、分錄、結案）用這個判斷「Purge 為 0 是否真的沒有值要清」。
/// </summary>
public static class ComputedValueProbe
{
    /// <summary>用串流計數的 <see cref="IDeletionRepository.CountLiveRecordsWithValueAsync"/>，空值定義與刪除規劃器一致（null、空白、空陣列）。</summary>
    public static async Task<bool> HasValuesAsync(IDeletionRepository data, Module module, IReadOnlyCollection<Field> fields, CancellationToken ct)
    {
        var keys = fields.Where(f => f.Type is FieldType.Formula or FieldType.Rollup).Select(f => f.Key).ToList();
        if (keys.Count == 0) return false;
        return (await data.CountLiveRecordsWithValueAsync(module.Id, keys, ct)).Values.Any(count => count > 0);
    }

    public static bool NothingPurged(PurgeCounts purge) => purge is { Records: 0, IndexRows: 0, HistoryRows: 0 };
}
