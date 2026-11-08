using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.Rollups;

/// <summary>
/// 定義變更觸發的整批重算要跳過的「已固定」記錄（設計 §10.4）：處於狀態機唯讀狀態，或已有事件事實的來源記錄。
/// 子記錄寫入觸發的單筆彙總重算不經過這裡，那是資料變動而不是定義變動。
/// </summary>
public sealed class FrozenRecords(Module module, IReadOnlySet<long> withFacts)
{
    public static async Task<FrozenRecords> LoadAsync(Module module, IEventFactRepository facts, CancellationToken ct) =>
        new(module, await facts.ListSourceIdsInModuleAsync(module.Id, ct));

    public bool Contains(Record record) =>
        withFacts.Contains(record.Id)
        || (module.Workflow is { } wf && wf.IsLocked(RecordValueProcessor.Parse(record.ValuesJson)));
}
