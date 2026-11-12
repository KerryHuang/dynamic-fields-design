using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.DeleteService.Data;

/// <summary>
/// 公式與彙總沒有歷程也沒有索引列，刪除它們時 Purge 全為 0，但未刪除記錄上可能有值會被清掉。
/// 受保護記錄的守衛（簽核中、分錄、結案）用這個判斷「Purge 為 0 是否真的沒有值要清」。
/// </summary>
public static class ComputedValueProbe
{
    public static async Task<bool> HasValuesAsync(IRecordRepository records, Module module, IReadOnlyCollection<Field> fields, CancellationToken ct)
    {
        var keys = fields.Where(f => f.Type is FieldType.Formula or FieldType.Rollup).Select(f => f.Key).ToList();
        if (keys.Count == 0) return false;
        return (await records.ListLiveValuesAsync(module.Id, ct))
            .Any(r => RecordValueProcessor.Parse(r.ValuesJson) is var v && keys.Any(k => v.TryGetValue(k, out var raw) && raw is { ValueKind: not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined) }));
    }

    public static bool NothingPurged(PurgeCounts purge) => purge is { Records: 0, IndexRows: 0, HistoryRows: 0 };
}
