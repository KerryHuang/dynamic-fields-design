using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;

namespace DynamicModules.Application.Dynamic.Rollups;

/// <summary>
/// 定義變更觸發的整批重算要跳過的「已固定」記錄（設計 §8.6 第 4 點、§10.4）：處於狀態機唯讀狀態、已有事件事實、簽核中、
/// 在簽核鎖定狀態的記錄，以及明細列所屬的表頭有事實、簽核中或在鎖定狀態（事實鎖與簽核鎖都在表頭上，§4.7）。
/// 子記錄寫入觸發的單筆彙總重算不經過這裡，那是資料變動而不是定義變動。由 <see cref="FrozenRecordsLoader"/> 建立。
/// </summary>
public sealed class FrozenRecords(
    Module module, IReadOnlySet<long> withFacts, IReadOnlySet<long> frozenHeaderIds, IReadOnlySet<long> pendingIds, ApprovalLock approvalLock)
{
    public bool Contains(Record record)
    {
        if (withFacts.Contains(record.Id) || pendingIds.Contains(record.Id)) return true;
        var values = RecordValueProcessor.Parse(record.ValuesJson);
        if (frozenHeaderIds.Count > 0 && ModuleRelations.DetailFieldOf(module) is { } headerField
            && values.TryGetValue(headerField.Key, out var raw) && raw is { ValueKind: System.Text.Json.JsonValueKind.Number } n
            && n.TryGetInt64(out var headerId) && frozenHeaderIds.Contains(headerId))
            return true;
        return module.Workflow is { } wf && (wf.IsLocked(values) || approvalLock.IsLockedState(wf.StateOf(values)));
    }
}
