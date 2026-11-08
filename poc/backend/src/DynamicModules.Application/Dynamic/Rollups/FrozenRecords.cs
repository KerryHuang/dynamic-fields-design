using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.Rollups;

/// <summary>
/// 定義變更觸發的整批重算要跳過的「已固定」記錄（設計 §10.4）：處於狀態機唯讀狀態、已有事件事實的來源記錄，
/// 以及明細列所屬的表頭已有事實（事實鎖在表頭上，§4.7）。
/// 子記錄寫入觸發的單筆彙總重算不經過這裡，那是資料變動而不是定義變動。
/// </summary>
public sealed class FrozenRecords(Module module, IReadOnlySet<long> withFacts, IReadOnlySet<long> headersWithFacts)
{
    public static async Task<FrozenRecords> LoadAsync(Module module, IEventFactRepository facts, CancellationToken ct) =>
        new(module, await facts.ListSourceIdsInModuleAsync(module.Id, ct),
            ModuleRelations.ParentKeyOf(module) is { } headerKey ? await facts.ListSourceIdsInModuleKeyAsync(headerKey, ct) : new HashSet<long>());

    public bool Contains(Record record)
    {
        if (withFacts.Contains(record.Id)) return true;
        var values = RecordValueProcessor.Parse(record.ValuesJson);
        if (headersWithFacts.Count > 0 && ModuleRelations.DetailFieldOf(module) is { } headerField
            && values.TryGetValue(headerField.Key, out var raw) && raw is { ValueKind: System.Text.Json.JsonValueKind.Number } n
            && n.TryGetInt64(out var headerId) && headersWithFacts.Contains(headerId))
            return true;
        return module.Workflow is { } wf && wf.IsLocked(values);
    }
}
